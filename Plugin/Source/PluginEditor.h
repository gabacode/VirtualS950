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

    juce::Slider gain { juce::Slider::RotaryHorizontalVerticalDrag,
                        juce::Slider::TextBoxBelow };
    juce::Label  gainLabel;
    juce::Label  patchLabel;
    juce::Label  voicesLabel;

    std::unique_ptr<juce::AudioProcessorValueTreeState::SliderAttachment> gainAttachment;

    JUCE_DECLARE_NON_COPYABLE_WITH_LEAK_DETECTOR (VirtualS950Editor)
};
