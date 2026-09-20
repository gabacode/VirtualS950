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

    setSize (420, 180);
    startTimerHz (10);
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
    g.drawText ("Akai S950 engine  -  placeholder sound until disks load",
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
    patchLabel.setBounds (r.removeFromTop (24));
    voicesLabel.setBounds (r.removeFromTop (20));
}
