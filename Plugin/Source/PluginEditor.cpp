#include "PluginEditor.h"

VirtualS950Editor::VirtualS950Editor (VirtualS950Processor& p)
    : AudioProcessorEditor (&p), processor (p)
{
    gain.setTextValueSuffix ("");
    addAndMakeVisible (gain);

    gainAttachment = std::make_unique<juce::AudioProcessorValueTreeState::SliderAttachment> (
        processor.parameters, "gain", gain);

    gainLabel.setText ("Gain", juce::dontSendNotification);
    gainLabel.setJustificationType (juce::Justification::centred);
    addAndMakeVisible (gainLabel);

    patchLabel.setJustificationType (juce::Justification::centredLeft);
    addAndMakeVisible (patchLabel);

    voicesLabel.setJustificationType (juce::Justification::centredLeft);
    voicesLabel.setColour (juce::Label::textColourId, juce::Colours::grey);
    addAndMakeVisible (voicesLabel);

    loadButton.onClick = [this] { openDisk(); };
    addAndMakeVisible (loadButton);

    programs.setTextWhenNoChoicesAvailable ("no disk loaded");
    programs.onChange = [this]
    {
        // The combo is filled with 1-based ids, which is JUCE's convention because 0 means
        // "nothing selected".
        processor.selectProgram (programs.getSelectedId() - 1);
    };
    addAndMakeVisible (programs);

    // Whatever the processor is already holding - this editor may well not be the first.
    refreshPrograms();

    // Big enough to hold the file browser, which opens inside this window rather than as a
    // dialog of its own - see openDisk.
    setSize (720, 500);
    startTimerHz (10);
}

/*
 * Pick an image.
 *
 * The browser opens INSIDE this window rather than as a native dialog, and that is the
 * whole point of it. JUCE puts a native chooser on the primary display:
 *
 *     auto mainMon = Desktop::getInstance().getDisplays().getPrimaryDisplay()->userBounds;
 *     setBounds (mainMon.getX() + mainMon.getWidth() / 4, ...)
 *
 * On a machine with two monitors six thousand pixels apart, a plugin on the second one
 * opened its file dialog on the first, where nobody was looking. From the DAW it was
 * indistinguishable from a button that did nothing.
 *
 * Parenting the browser into the editor removes the whole class of problem rather than that
 * one instance of it: it cannot land on another screen, it cannot hide behind a DAW window
 * that is always on top, and it cannot take focus away from the host. The cost is that it
 * looks like JUCE rather than like Windows, which for a plugin is the right way round.
 *
 * Either container: a plain sector image, or the .hfe that archived floppies come in.
 */
void VirtualS950Editor::openDisk()
{
    // Somewhere useful to start, so there is no navigating on the first try.
    auto start = juce::File::getSpecialLocation (juce::File::userDocumentsDirectory)
                     .getChildFile ("S950 images");

    if (! start.isDirectory())
        start = juce::File::getSpecialLocation (juce::File::userDocumentsDirectory);

    chooser = std::make_unique<juce::FileChooser> ("Open an Akai S950 disk image",
                                                   start,
                                                   "*.hfe;*.img",
                                                   false,      // not the OS dialog - see above
                                                   false,
                                                   this);      // live in this window

    const auto flags = juce::FileBrowserComponent::openMode
                     | juce::FileBrowserComponent::canSelectFiles;

    chooser->launchAsync (flags, [this] (const juce::FileChooser& fc)
    {
        const auto file = fc.getResult();
        if (file == juce::File()) return;

        juce::String error;

        if (! processor.loadDisk (file, error))
        {
            juce::NativeMessageBox::showMessageBoxAsync (
                juce::MessageBoxIconType::WarningIcon,
                "Could not open that disk",
                error);
            return;
        }

        refreshPrograms();
    });
}

VirtualS950Editor::~VirtualS950Editor()
{
    stopTimer();
}

void VirtualS950Editor::refreshPrograms()
{
    /*
     * dontSendNotification throughout: filling the box must not look like someone choosing
     * from it. Without that, rebuilding the list would call selectProgram and rebuild the
     * patch - and putting the selection back would do it a second time, under whatever is
     * currently sounding.
     */
    programs.clear (juce::dontSendNotification);

    const auto names = processor.getProgramNames();

    for (int i = 0; i < names.size(); ++i)
        programs.addItem (names[i], i + 1);

    const int chosen = processor.getSelectedProgram();

    if (chosen >= 0 && chosen < names.size())
        programs.setSelectedId (chosen + 1, juce::dontSendNotification);
}

void VirtualS950Editor::timerCallback()
{
    /*
     * Follow the processor if it changed underneath us.
     *
     * The disk and the programme can now move without this window doing it: the host's own
     * program chooser, a MIDI program change, or a saved set being restored after the
     * editor was already built. One integer compare ten times a second, against a counter
     * the processor bumps, catches all three without any of them having to know this
     * window exists.
     */
    const int generation = processor.getDiskGeneration();

    if (generation != seenGeneration)
    {
        seenGeneration = generation;
        refreshPrograms();
        repaint();                  // the disk name is painted, not a label
    }

    const int voices = processor.getActiveVoices();

    patchLabel.setText (processor.getPatchName(), juce::dontSendNotification);

    /*
     * The S950 had eight voices, and so does this - see Engine::Polyphony.
     *
     * A disk recovered with bad or missing sectors says so beside them. An archived floppy
     * is thirty years old and some of them do not read cleanly; finding that out from a
     * label beats finding it out from a hole in a take.
     */
    juce::String state = juce::String (voices) + " of 8 voices";

    const int bad     = processor.getBadSectors();
    const int missing = processor.getMissingSectors();

    if (bad > 0 || missing > 0)
        state << "   -   recovered with " << bad << " bad and " << missing << " missing sectors";

    voicesLabel.setText (state, juce::dontSendNotification);
}

void VirtualS950Editor::paint (juce::Graphics& g)
{
    g.fillAll (getLookAndFeel().findColour (juce::ResizableWindow::backgroundColourId));

    g.setColour (juce::Colours::white);
    g.setFont (juce::FontOptions (22.0f));
    g.drawText ("VirtualS950", 16, 12, getWidth() - 32, 28,
                juce::Justification::centredLeft, true);

    /*
     * When this copy was compiled.
     *
     * A host holds a plugin's binary open for as long as a set using it is loaded, so a
     * rebuild can quietly fail to install and the window looks identical either way. That
     * has now cost three rounds of "it still does not work" on a build that was never the
     * one running. The C# editor carries the same stamp for the same reason.
     */
    g.setColour (juce::Colours::darkgrey);
    g.setFont (juce::FontOptions (11.0f));
    g.drawText (juce::String (__DATE__) + "  " + __TIME__,
                16, 18, getWidth() - 32, 18,
                juce::Justification::centredRight, true);

    // The licence asks an interactive program to say so where it can be seen.
    g.setColour (juce::Colours::darkgrey);
    g.setFont (juce::FontOptions (10.0f));
    g.drawText (juce::CharPointer_UTF8 ("Copyright \xc2\xa9 2026 Simon Moscrop  -  AGPLv3, "
                                        "no warranty  -  github.com/simozzer/VirtualS950"),
                16, getHeight() - 22, getWidth() - 32, 16,
                juce::Justification::centredRight, true);

    g.setColour (juce::Colours::grey);
    g.setFont (juce::FontOptions (13.0f));

    const auto disk = processor.getDiskName();
    g.drawText (disk.isEmpty() ? "no disk  -  placeholder sound"
                               : juce::File (disk).getFileName(),
                16, 40, getWidth() - 32, 20,
                juce::Justification::centredLeft, true);
}

void VirtualS950Editor::resized()
{
    auto r = getLocalBounds().reduced (16);
    r.removeFromTop (48);                       // the title painted above

    auto knob = r.removeFromRight (110);
    gainLabel.setBounds (knob.removeFromBottom (20));
    gain.setBounds (knob);

    r.removeFromRight (16);

    auto row = r.removeFromTop (28);
    loadButton.setBounds (row.removeFromLeft (110));
    row.removeFromLeft (8);
    programs.setBounds (row);

    r.removeFromTop (10);
    patchLabel.setBounds (r.removeFromTop (24));
    voicesLabel.setBounds (r.removeFromTop (20));
}
