using Adrenak.UniVoice;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using Unity.Multiplayer.Tools.NetStatsMonitor;
using Unity.Netcode;
using UnityEngine;

public enum MicrophoneQuality
{
    VERYLOW,
    HIGH,
    VERYHIGH

}
[RequireComponent(typeof(AudioSource))]
public class AudioController : NetworkBehaviour
{
    //Audio source with serialized field for networking
    [SerializeField]
    private AudioSource source = null;

    //Key to use for push to talk functionality
    public KeyCode pushToTalkKey;

    //So we can know when the player is talking
    private bool isPlaying;

    public bool ptt = false;

    public bool useMic = true;
    private int lastSample = 0;
    private AudioClip microphoneClip;
    private int microphonePosition = 0;
    private const int chunkSize = 1024;
    public int samplingRate = 44100;
    private Queue<AudioClip> audioClipQueue = new Queue<AudioClip>();
    [SerializeField]
    private float HoldTime = 2.0f; // Time to keep transmitting after speech stops, in seconds
    [SerializeField]
    private float SpeechThreshold = 0.01f; // Threshold for speech detection
    [SerializeField]
    private float holdTimer = 0f;
    [SerializeField]
    private bool isSpeechDetected = false;

    private List<float> audioBuffer = new List<float>();
    private int bufferSize = 4096; // Adjust the buffer size as needed


    void Start()
    {
        if (!IsOwner)
        {
            return;
        }
        //Set isn't playing at start
        this.isPlaying = false;

        //Get the audio source
        source = GetComponent<AudioSource>();
        microphoneClip = Microphone.Start(null, true, 1, samplingRate);

        StartCoroutine(ProcessMicrophoneAudio());

    }

    private void FixedUpdate()
    {
        if (audioBuffer.Count >= bufferSize)
        {
            VoiceClientRPC(ConvertAudioSamplesToBytes(audioBuffer.ToArray()));
        }
    }
    void ProcessAndPlayBuffer(byte[] audioData)
    {
        if (audioBuffer.Count > 0)
        {
            AudioClip newClip = ConvertBytesToAudioClip(audioData, 1, samplingRate);

            // Clear the buffer
            audioBuffer.Clear();


            // Play the buffered audio clip
            source.PlayOneShot(newClip);
        }
    }
    IEnumerator ChangePPTAfterSeconds(int seconds)
    {
        yield return new WaitForSeconds(seconds);
        ptt = false;
        microphoneClip = null;
    }
    private void Update()
    {
        if (!IsOwner)
        {
            return;
        }
        if (Input.GetKeyDown(pushToTalkKey))
        {
            ptt = true;
            microphoneClip = Microphone.Start(null, true, 1, samplingRate);
        }
        else if (Input.GetKeyUp(pushToTalkKey))
        {        
            StartCoroutine(ChangePPTAfterSeconds(3));
        }
   
        if (holdTimer > 0)
        {
            holdTimer -= Time.deltaTime;
            if (holdTimer <= 0)
            {
                isSpeechDetected = false;
                holdTimer = 0;
            }
        }
        // Process and play audio from the buffer when it reaches a certain size


    }
 
    private void PlayNextClip()
    {
        if (audioClipQueue.Count > 0)
        {
            AudioClip clipToPlay = audioClipQueue.Dequeue();
            source.PlayOneShot(clipToPlay);
        }
    }

    IEnumerator ProcessMicrophoneAudio()
    {

        while (Microphone.IsRecording(null))
        {
            if (!ptt)
            {
                break;
            }

            yield return new WaitForSeconds(1f); // Process audio every 0.1 seconds

            try
            {
                int currentMicPosition = Microphone.GetPosition(null);
                if (currentMicPosition < microphonePosition)
                {
                    // Process remaining data before the loop
                    ProcessAudioChunk(microphonePosition, microphoneClip.samples - microphonePosition);

                    // Reset position after loop
                    microphonePosition = 0;
                }

                int dataLength = currentMicPosition - microphonePosition - 0;
                if (dataLength >= chunkSize)
                {
                    // Process available data in chunkSize
                    ProcessAudioChunk(microphonePosition, chunkSize);
                    microphonePosition += chunkSize;

                    // Handle wrapping of microphonePosition
                    if (microphonePosition >= microphoneClip.samples)
                    {
                        microphonePosition -= microphoneClip.samples;
                    }
                }
            }
            catch (Exception)
            {

                break;
            }
          
        }
        yield return new WaitForSeconds(0.1f);
        StartCoroutine(ProcessMicrophoneAudio());

    }

    void ProcessAudioChunk(int startPosition, int length)
    {
        // Existing code to get the samples
        float[] sample = new float[length * microphoneClip.channels];
        microphoneClip.GetData(sample, startPosition);

        // Check if the current chunk contains speech
        if (IsSpeech(sample) || holdTimer > 0)
        {
            if (IsSpeech(sample))
            {
                isSpeechDetected = true;
                holdTimer = HoldTime;
            }


            // Convert samples to bytes
            byte[] audioData = ConvertAudioSamplesToBytes(sample);

            // Here, you would send audioData to your voice chat network

            // For demonstration, converting bytes back to audio and playing it
            AudioClip newClip = ConvertBytesToAudioClip(audioData, microphoneClip.channels, microphoneClip.frequency);

            // Enqueue the new clip
            audioClipQueue.Enqueue(newClip);
            audioBuffer.AddRange(sample);
        }
        else
        {
            isSpeechDetected = false;
            // Optionally handle non-speech scenario
        }

    }
    int FindZeroCrossing(float[] samples, int start, bool forward = true)
    {
        int direction = forward ? 1 : -1;
        for (int i = start; forward ? i < samples.Length : i >= 0; i += direction)
        {
            if (samples[i] * samples[i + direction] <= 0) // Crossing zero
            {
                return i;
            }
        }
        return start; // Default to the original start position if no zero crossing is found
    }
    void ApplyFadeIn(float[] samples, int fadeLength)
    {
        for (int i = 0; i < fadeLength && i < samples.Length; i++)
        {
            float fadeFactor = (float)i / fadeLength;
            samples[i] *= fadeFactor;
        }
    }
    bool IsSpeech(float[] samples)
    {
        float sum = 0f;

        for (int i = 0; i < samples.Length; i++)
        {
            sum += Mathf.Abs(samples[i]);
        }

        float average = sum / samples.Length;
        return average > SpeechThreshold;
    }

    byte[] ConvertAudioSamplesToBytes(float[] samples)
    {
        byte[] byteArray = new byte[samples.Length * 4];
        System.Buffer.BlockCopy(samples, 0, byteArray, 0, byteArray.Length);
        return byteArray;
    }
    AudioClip ConvertBytesToAudioClip(byte[] byteArray, int channels, int frequency)
    {
        float[] samples = new float[byteArray.Length / 4];
        System.Buffer.BlockCopy(byteArray, 0, samples, 0, byteArray.Length);

        AudioClip newClip = AudioClip.Create("New AudioClip", samples.Length / channels, channels, frequency, false);
        newClip.SetData(samples, 0);
        return newClip;
    }




    [ClientRpc]
    void VoiceClientRPC(byte[] bytes)
    {

        Debug.Log(bytes);


        Debug.Log("called RPC");
    }


    //Crossfade shenanigans


}