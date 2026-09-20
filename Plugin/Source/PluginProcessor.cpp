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

void VirtualS950Processor::getStateInformation (juce::MemoryBlock& destination)
{
    /*
     * Only the parameters so far.
     *
     * When disks can be read this has to carry the programme as well, and a file path will
     * not do it: the library moves and the project stops working. An S950 image is 800 x
     * 1024 bytes, so the whole disk can go in here and a saved project can never be missing
     * the sound it was made with.
     */
    if (auto state = parameters.copyState().createXml())
        copyXmlToBinary (*state, destination);
}

void VirtualS950Processor::setStateInformation (const void* data, int size)
{
    if (auto xml = getXmlFromBinary (data, size))
        if (xml->hasTagName (parameters.state.getType()))
            parameters.replaceState (juce::ValueTree::fromXml (*xml));
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

    juce::StringArray names;
    for (const auto& e : opened->getEntries())
        if (e.type == 'P')
            names.add (juce::String (e.name));

    if (names.isEmpty())
    {
        error = "that disk has no programmes on it";
        return false;
    }

    disk         = std::move (opened);
    programNames = names;
    selectedProgram = -1;

    selectProgram (0);
    return true;
}

juce::StringArray VirtualS950Processor::getProgramNames() const
{
    return programNames;
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
