#pragma once

#include <juce_audio_processors/juce_audio_processors.h>

#include "PluginProcessor.h"

/*
 * The plugin's window.
 *
 * Deliberately almost nothing: a gain slider, what is loaded, and how many voices are
 * sounding. The editor that matters is AkaiS950Studio, which is where disks are opened and
 * keygroups are edited; this is what a host needs to show while the instrument plays.
 *
 * The voice count is here because it answers the first question anyone asks of a plugin
 * that is not making a sound - is it getting the notes? - without a debugger.
 */
class VirtualS950Editor : public juce::AudioProcessorEditor,
                          private juce::Timer
{
public:
    explicit VirtualS950Editor (VirtualS950Processor&);
    ~VirtualS950Editor() override;

    void paint (juce::Graphics&) override;
    void resized() override;

private:
    void timerCallback() override;

    VirtualS950Processor& processor;

    /// The processor generation this window last caught up with. See timerCallback.
    int seenGeneration = -1;

    void openDisk();

    /*
     * Put the loaded disk's programmes in the box.
     *
     * Called when the editor is built as well as when a disk is opened, and that is the
     * point of it being a method. A host destroys and recreates the editor every time the
     * plugin's window is closed and reopened, while the processor - and the disk it is
     * holding - carries on untouched. An editor that only filled this list when a disk was
     * chosen came back empty, so changing programme meant opening the same disk again.
     */
    void refreshPrograms();

    juce::Slider gain { juce::Slider::RotaryHorizontalVerticalDrag,
                        juce::Slider::TextBoxBelow };
    juce::Label  gainLabel;
    juce::Label  patchLabel;
    juce::Label  voicesLabel;

    juce::TextButton loadButton { "Load disk..." };
    juce::ComboBox   programs;

    /// Held for as long as the dialog is open: launchAsync returns at once, and a chooser
    /// that goes out of scope takes its window with it.
    std::unique_ptr<juce::FileChooser> chooser;

    std::unique_ptr<juce::AudioProcessorValueTreeState::SliderAttachment> gainAttachment;

    JUCE_DECLARE_NON_COPYABLE_WITH_LEAK_DETECTOR (VirtualS950Editor)
};
