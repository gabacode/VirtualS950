#include "PluginProcessor.h"
#include "PluginEditor.h"

#include <cmath>

namespace
{
    constexpr double Pi = 3.14159265358979323846;
}

// ------------------------------------------------------------------------ parameters

juce::AudioProcessorValueTreeState::ParameterLayout
VirtualS950Processor::describeParameters()
{
    juce::AudioProcessorValueTreeState::ParameterLayout layout;

    /*
     * One parameter for now, and it is the one that matters: eight voices of a hot sample
     * with a zone trim will clip, which is what the machine's own master level is for.
     *
     * Everything else about the sound comes off the disk rather than out of a slider, which
     * is the whole point of the instrument - so the automatable surface stays small until
     * there is a reason for it to grow.
     */
    layout.add (std::make_unique<juce::AudioParameterFloat> (
        juce::ParameterID { "gain", 1 },
        "Gain",
        juce::NormalisableRange<float> (0.0f, 2.0f, 0.0f, 0.5f),
        0.7f));

    return layout;
}

// ---------------------------------------------------------------------- the placeholder

s950::PatchPtr VirtualS950Processor::makePlaceholderPatch()
{
    auto sound = std::make_shared<s950::Sound>();
    sound->name       = "SAW";
    sound->sourceRate = 48000;
    sound->rootPitch  = 60.0;

    /*
     * Eight cycles rather than one, so the loop length is a whole number of samples AND a
     * whole number of cycles. 48000 / 261.626 is 183.49 samples for middle C: rounding that
     * to 183 would put the placeholder five cents flat, where eight cycles rounded to 1468
     * is out by three hundredths of one.
     */
    const int cycles = 8;
    const int words  = 1468;

    sound->audio.resize (static_cast<size_t> (words));

    for (int i = 0; i < words; ++i)
    {
        const double phase = std::fmod (i * cycles / static_cast<double> (words), 1.0);
        sound->audio[static_cast<size_t> (i)] = static_cast<float> (phase * 2.0 - 1.0);
    }

    sound->loops    = true;
    sound->loopFrom = 0;
    sound->loopTo   = words;

    auto p = std::make_shared<s950::Patch>();
    p->name = "Placeholder saw";

    s950::KeygroupPatch kg;
    kg.lowKey        = 0;
    kg.highKey       = 127;
    kg.keygroupIndex = 0;
    kg.sound         = sound;

    kg.vcaAttack  = 0;
    kg.vcaDecay   = 0;
    kg.vcaSustain = 99;
    kg.vcaRelease = 25;

    kg.zoneFilter  = 70;        // something to hear the filter doing its job
    kg.keyToFilter = 50;        // measured: 50 is one-for-one tracking
    kg.lfoDesync   = true;

    p->keygroups.push_back (kg);
    return p;
}

// ------------------------------------------------------------------------ the plugin

VirtualS950Processor::VirtualS950Processor()
    : AudioProcessor (BusesProperties().withOutput ("Output",
                                                    juce::AudioChannelSet::stereo(),
                                                    true)),
      parameters (*this, nullptr, "state", describeParameters())
{
    gainParameter = parameters.getRawParameterValue ("gain");
    patch         = makePlaceholderPatch();

    /*
     * The engine hands a replaced programme back to be freed, and it must be freed on this
     * thread rather than in the audio callback. Nothing else would ever ask.
     */
    startTimer (500);
}

VirtualS950Processor::~VirtualS950Processor()
{
    stopTimer();
}

void VirtualS950Processor::timerCallback()
{
    if (engine != nullptr)
        engine->collectRetiredPatch();
}

void VirtualS950Processor::prepareToPlay (double sampleRate, int samplesPerBlock)
{
    /*
     * Rebuilt rather than retuned: a voice works out its pitch, its filter ceiling and its
     * LFO step from the rate it was started at, so there is no honest way to change the rate
     * under a sounding note. The host only calls this while stopped.
     */
    engine = std::make_unique<s950::Engine> (sampleRate);
    engine->setPatch (patch);

    // processBlock must not allocate, so the scratch buffer is sized here. A little over,
    // because some hosts hand over a longer block than they promised.
    mono.setSize (1, juce::jmax (samplesPerBlock, 1024), false, true, true);
}

bool VirtualS950Processor::isBusesLayoutSupported (const BusesLayout& layouts) const
{
    const auto out = layouts.getMainOutputChannelSet();
    return out == juce::AudioChannelSet::mono() || out == juce::AudioChannelSet::stereo();
}

double VirtualS950Processor::getTailLengthSeconds() const
{
    // The longest release the machine can be told to do, so a host does not cut a note off.
    return s950::cal::envSeconds (99);
}

void VirtualS950Processor::processBlock (juce::AudioBuffer<float>& buffer,
                                         juce::MidiBuffer& midi)
{
    juce::ScopedNoDenormals noDenormals;

    const int count = buffer.getNumSamples();
    buffer.clear();

    if (engine == nullptr || count <= 0)
        return;

    engine->gain.store (gainParameter != nullptr ? gainParameter->load() : 0.7f,
                        std::memory_order_relaxed);

    /*
     * Every message, with where in this block it happens.
     *
     * This is what the sample offsets in Engine were added for. JUCE hands each message
     * over with its position already worked out, so passing it on costs nothing and a note
     * lands where the host put it rather than on the block boundary.
     */
    for (const auto meta : midi)
    {
        const auto m  = meta.getMessage();
        const int  at = meta.samplePosition;

        if (m.isNoteOn())
            engine->noteOn (m.getNoteNumber(), m.getVelocity(), at);
        else if (m.isNoteOff())
            engine->noteOff (m.getNoteNumber(), at);
        else if (m.isController() && m.getControllerNumber() == 1)
            engine->modwheel (m.getControllerValue(), at);
        else if (m.isAllNotesOff() || m.isAllSoundOff())
            engine->allNotesOff (at);
    }

    // The engine is mono - the machine was, through one output - so it renders once and the
    // same signal goes to both channels.
    if (mono.getNumSamples() < count)
        mono.setSize (1, count, false, true, true);     // only if a host broke its promise

    float* scratch = mono.getWritePointer (0);
    engine->render (scratch, count);

    for (int ch = 0; ch < buffer.getNumChannels(); ++ch)
        buffer.copyFrom (ch, 0, scratch, count);
}

// ------------------------------------------------------------------------- the state

/*
 * WHAT A SAVED PROJECT CARRIES
 *
 * The whole disk, not a path to it.
 *
 * A path is smaller and it is what most plugins store, and it breaks: the library gets
 * moved or renamed, the project goes to somebody else, the drive letter changes, and the
 * song opens silent. For a sampler that is worse than for most plugins, because the sound
 * IS the disk - there is nothing to fall back on.
 *
 * An S950 floppy is 800 x 1024 bytes. Compressed and encoded it comes to a few hundred
 * kilobytes inside the project, which is nothing beside the audio a session already holds,
 * and it means a saved song can never lose the sound it was made with - on this machine or
 * any other.
 *
 * The sectors are stored rather than the .hfe they may have arrived in: decoding is
 * deterministic and one way, so keeping the result means reopening does no MFM work and
 * cannot come out differently from the day it was saved.
 */
void VirtualS950Processor::getStateInformation (juce::MemoryBlock& destination)
{
    auto state = parameters.copyState();
    auto xml   = state.createXml();

    if (xml == nullptr)
        return;

    if (disk != nullptr && ! programNames.isEmpty())
    {
        auto* node = xml->createNewChildElement ("DISK");

        node->setAttribute ("name",    juce::String (disk->getName()));
        node->setAttribute ("path",    diskPath);
        node->setAttribute ("program", selectedProgram);

        // Also by name: if a disk is ever replaced by an edited version with the
        // programmes in a different order, the name is what the musician meant.
        if (selectedProgram >= 0 && selectedProgram < programNames.size())
            node->setAttribute ("programName", programNames[selectedProgram]);

        const auto& image = disk->getImage();

        juce::MemoryOutputStream packed;
        {
            juce::GZIPCompressorOutputStream zip (packed, 9);
            zip.write (image.data(), image.size());
        }

        node->setAttribute ("bytes",  static_cast<int> (image.size()));
        node->addTextElement (juce::Base64::toBase64 (packed.getData(), packed.getDataSize()));
    }

    copyXmlToBinary (*xml, destination);
}

void VirtualS950Processor::setStateInformation (const void* data, int size)
{
    auto xml = getXmlFromBinary (data, size);

    if (xml == nullptr || ! xml->hasTagName (parameters.state.getType()))
        return;

    /*
     * The disk rides as a child of the parameter tree, so what is wanted is copied out and
     * the element taken away before the rest is handed to the APVTS - which knows nothing
     * about it and would only carry it around inside the parameter state for ever.
     */
    juce::String diskName, savedPath, wantedProgram, encoded;
    int  savedIndex = 0;
    bool haveDisk   = false;

    if (auto* found = xml->getChildByName ("DISK"))
    {
        diskName      = found->getStringAttribute ("name");
        savedPath     = found->getStringAttribute ("path");
        wantedProgram = found->getStringAttribute ("programName");
        savedIndex    = found->getIntAttribute ("program", 0);
        encoded       = found->getAllSubText().trim();
        haveDisk      = true;

        xml->removeChildElement (found, true);
    }

    parameters.replaceState (juce::ValueTree::fromXml (*xml));

    if (! haveDisk || encoded.isEmpty())
        return;

    juce::MemoryOutputStream packed;
    if (! juce::Base64::convertFromBase64 (packed, encoded))
        return;

    juce::MemoryInputStream          source (packed.getData(), packed.getDataSize(), false);
    juce::GZIPDecompressorInputStream unzip (source);

    juce::MemoryOutputStream sectors;
    sectors.writeFromInputStream (unzip, -1);

    if (sectors.getDataSize() == 0)
        return;

    const auto* first = static_cast<const unsigned char*> (sectors.getData());
    std::vector<unsigned char> bytes (first, first + sectors.getDataSize());

    auto restored = std::make_unique<s950::Disk>();
    std::string why;

    if (! restored->loadBytes (diskName.toStdString(), std::move (bytes), why))
        return;

    juce::String error;
    if (! adoptDisk (std::move (restored), error))
        return;

    diskPath = savedPath;

    /*
     * Back to the programme that was playing.
     *
     * By name first: a disk edited since the song was saved can have its programmes in a
     * different order, and the name is what was meant. The index is the fallback, for a
     * programme that has since been renamed.
     */
    const int index = programNames.indexOf (wantedProgram);

    selectProgram (index >= 0 ? index : savedIndex);
}

// ---------------------------------------------------------------------------- disks

bool VirtualS950Processor::loadDisk (const juce::File& file, juce::String& error)
{
    auto opened = std::make_unique<s950::Disk>();

    std::string why;
    if (! opened->loadFile (file.getFullPathName().toStdString(), why))
    {
        error = juce::String (why);
        return false;
    }

    if (! adoptDisk (std::move (opened), error))
        return false;

    diskPath = file.getFullPathName();
    return true;
}

bool VirtualS950Processor::adoptDisk (std::unique_ptr<s950::Disk> opened, juce::String& error)
{
    if (opened == nullptr) { error = "no disk"; return false; }

    juce::StringArray names;
    for (const auto& e : opened->getEntries())
        if (e.type == 'P')
            names.add (juce::String (e.name));

    if (names.isEmpty())
    {
        error = "that disk has no programmes on it";
        return false;
    }

    disk            = std::move (opened);
    diskPath        = {};
    programNames    = names;
    selectedProgram = -1;

    selectProgram (0);
    diskGeneration.fetch_add (1, std::memory_order_relaxed);

    /*
     * A new disk is a new list of programmes, and the host is showing the old one.
     *
     * parameterInfoChanged is what makes a host re-read the list; programChanged only says
     * which of them is current. Hosts vary in how far they go - some rebuild the chooser,
     * some only notice on reload - so the plugin's own combo box stays the reliable route
     * and this is the convenience.
     */
    updateHostDisplay (juce::AudioProcessorListener::ChangeDetails {}
                           .withProgramChanged (true)
                           .withParameterInfoChanged (true));

    return true;
}

juce::StringArray VirtualS950Processor::getProgramNames() const
{
    return programNames;
}

// --------------------------------------------------- the disk's programmes, as the host's

/*
 * A host insists on at least one programme, so with no disk loaded there is exactly one and
 * it is the placeholder. Saying zero here makes some hosts unhappy and others hide the
 * chooser entirely.
 */
int VirtualS950Processor::getNumPrograms()
{
    return juce::jmax (1, programNames.size());
}

int VirtualS950Processor::getCurrentProgram()
{
    return juce::jmax (0, selectedProgram);
}

void VirtualS950Processor::setCurrentProgram (int index)
{
    selectProgram (index);
}

const juce::String VirtualS950Processor::getProgramName (int index)
{
    if (juce::isPositiveAndBelow (index, programNames.size()))
        return programNames[index];

    return programNames.isEmpty() ? "Placeholder saw" : juce::String();
}

juce::String VirtualS950Processor::getDiskName() const
{
    return disk != nullptr ? juce::String (disk->getName()) : juce::String();
}

int VirtualS950Processor::getBadSectors() const
{
    return disk != nullptr ? disk->getBadCrcSectors() : 0;
}

int VirtualS950Processor::getMissingSectors() const
{
    return disk != nullptr ? disk->getMissingSectors() : 0;
}

void VirtualS950Processor::selectProgram (int index)
{
    if (disk == nullptr || index < 0 || index >= programNames.size())
        return;

    // The nth programme in directory order, which is the order getProgramNames built.
    const s950::Disk::Entry* entry = nullptr;
    int seen = 0;

    for (const auto& e : disk->getEntries())
    {
        if (e.type != 'P') continue;
        if (seen++ == index) { entry = &e; break; }
    }

    if (entry == nullptr) return;

    auto built = disk->buildPatch (*entry);
    if (built == nullptr || built->keygroups.empty())
        return;                                    // leave what is playing alone

    selectedProgram = index;
    patch = built;

    if (engine != nullptr)
        engine->setPatch (patch);

    diskGeneration.fetch_add (1, std::memory_order_relaxed);
}

// ------------------------------------------------------------------- for the editor

int VirtualS950Processor::getActiveVoices() const
{
    return engine != nullptr ? engine->getActiveVoices() : 0;
}

juce::String VirtualS950Processor::getPatchName() const
{
    return patch != nullptr ? juce::String (patch->name) : juce::String ("nothing loaded");
}

juce::AudioProcessorEditor* VirtualS950Processor::createEditor()
{
    return new VirtualS950Editor (*this);
}

// -------------------------------------------------------------------------- the hook

juce::AudioProcessor* JUCE_CALLTYPE createPluginFilter()
{
    return new VirtualS950Processor();
}
