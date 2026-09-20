#pragma once

#include <juce_audio_processors/juce_audio_processors.h>

#include "S950/Engine.h"

#include <memory>

/*
 * The plugin.
 *
 * Thin on purpose. Everything that decides how the instrument sounds is in Source/S950,
 * which knows nothing about JUCE and is checked against the C# engine it was ported from.
 * What is left here is the three things a host actually needs: give me a block, here are
 * the notes that happen inside it, and remember this when you save.
 */
class VirtualS950Processor : public juce::AudioProcessor,
                             private juce::Timer
{
public:
    VirtualS950Processor();
    ~VirtualS950Processor() override;

    void prepareToPlay (double sampleRate, int samplesPerBlock) override;
    void releaseResources() override {}
    bool isBusesLayoutSupported (const BusesLayout& layouts) const override;
    void processBlock (juce::AudioBuffer<float>&, juce::MidiBuffer&) override;

    juce::AudioProcessorEditor* createEditor() override;
    bool hasEditor() const override { return true; }

    const juce::String getName() const override { return JucePlugin_Name; }

    bool acceptsMidi() const override  { return true; }
    bool producesMidi() const override { return false; }
    bool isMidiEffect() const override { return false; }
    double getTailLengthSeconds() const override;

    int getNumPrograms() override { return 1; }
    int getCurrentProgram() override { return 0; }
    void setCurrentProgram (int) override {}
    const juce::String getProgramName (int) override { return "Default"; }
    void changeProgramName (int, const juce::String&) override {}

    void getStateInformation (juce::MemoryBlock&) override;
    void setStateInformation (const void*, int) override;

    // ----------------------------------------------------------------- for the editor

    juce::AudioProcessorValueTreeState parameters;

    /// How many voices are sounding. Read by the editor on a timer; approximate by nature.
    int getActiveVoices() const;

    /// What is loaded, for the editor to name.
    juce::String getPatchName() const;

private:
    void timerCallback() override;

    static juce::AudioProcessorValueTreeState::ParameterLayout describeParameters();

    /*
     * A sound to play until disks can be read.
     *
     * Eight cycles of a sawtooth, looped, so a key transposes it the way a keygroup
     * transposes a sample. It is a placeholder and says so in the editor - but it means the
     * whole chain from the host's MIDI to the speakers can be judged before AkaiS950List is
     * ported, which is worth more than an instrument that is silent until everything works.
     */
    static s950::PatchPtr makePlaceholderPatch();

    /*
     * The engine is rebuilt when the host changes the sample rate, because a voice works
     * out its pitch and its filter from the rate it was started at.
     */
    std::unique_ptr<s950::Engine> engine;
    s950::PatchPtr               patch;

    /// The engine renders one channel; the host usually wants two. Sized in prepareToPlay,
    /// because processBlock is not allowed to allocate.
    juce::AudioBuffer<float> mono;

    std::atomic<float>* gainParameter = nullptr;

    JUCE_DECLARE_NON_COPYABLE_WITH_LEAK_DETECTOR (VirtualS950Processor)
};
