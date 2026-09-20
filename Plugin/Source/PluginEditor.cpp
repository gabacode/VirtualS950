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

    setSize (460, 230);
    startTimerHz (10);
}

/*
 * Pick an image.
 *
 * Raw sector images only so far. The library this was written against is .hfe, which is the
 * same 800K of sectors wrapped in a bit-level encoding of what a floppy controller would
 * see off the disk; decoding that is a job of its own, and the editor can already write a
 * raw image out in the meantime.
 */
void VirtualS950Editor::openDisk()
{
    chooser = std::make_unique<juce::FileChooser> ("Open an Akai S950 disk image",
                                                   juce::File(),
                                                   "*.img");

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
