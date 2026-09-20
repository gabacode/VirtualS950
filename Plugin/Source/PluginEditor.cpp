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
 * Raw sector images only so far. The library this was written against is .hfe, the same
 * 800K wrapped in a bit-level encoding of what a floppy controller sees; the editor writes
 * raw images in the meantime.
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
                                                   "*.img",
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

        programs.clear (juce::dontSendNotification);

        const auto names = processor.getProgramNames();
        for (int i = 0; i < names.size(); ++i)
            programs.addItem (names[i], i + 1);

        programs.setSelectedId (processor.getSelectedProgram() + 1,
                                juce::dontSendNotification);
    });
}

VirtualS950Editor::~VirtualS950Editor()
{
    stopTimer();
}

void VirtualS950Editor::timerCallback()
{
    const int voices = processor.getActiveVoices();

    patchLabel.setText (processor.getPatchName(), juce::dontSendNotification);

    // The S950 had eight, and so does this - see Engine::Polyphony.
    voicesLabel.setText (juce::String (voices) + " of 8 voices",
                         juce::dontSendNotification);
}

void VirtualS950Editor::paint (juce::Graphics& g)
{
    g.fillAll (getLookAndFeel().findColour (juce::ResizableWindow::backgroundColourId));

    g.setColour (juce::Colours::white);
    g.setFont (juce::FontOptions (22.0f));
    g.drawText ("VirtualS950", 16, 12, getWidth() - 32, 28,
                juce::Justification::centredLeft, true);

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
