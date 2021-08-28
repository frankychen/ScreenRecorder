﻿using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ScreenRecorder.AudioSource
{
    public sealed class LoopbackAudioSource : IAudioSource, IDisposable
    {
        private class NotificationClient : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
        {
            private AutoResetEvent needToReset;
            public NotificationClient(ref AutoResetEvent _needToReset)
            {
                needToReset = _needToReset;
            }

            void NAudio.CoreAudioApi.Interfaces.IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
            {
            }

            void NAudio.CoreAudioApi.Interfaces.IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
            void NAudio.CoreAudioApi.Interfaces.IMMNotificationClient.OnDeviceRemoved(string deviceId)
            {

            }

            void NAudio.CoreAudioApi.Interfaces.IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render && role == Role.Console)
                {
                    needToReset?.Set();
                }
            }
            void NAudio.CoreAudioApi.Interfaces.IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }

        public event NewAudioPacketEventHandler NewAudioPacket;

        private Thread workerThread;
        private ManualResetEvent needToStop;
        private int sampleRate, channels, bitsPerSample;

        public LoopbackAudioSource()
        {
            needToStop = new ManualResetEvent(false);
            workerThread = new Thread(new ThreadStart(WorkerThreadHandler)) { Name = "LoopbackAudioSource", IsBackground = true };
            workerThread.Start();
        }

        private void WorkerThreadHandler()
        {
            WasapiLoopbackCapture waveIn = null;

            while (!needToStop.WaitOne(0, false))
            {
                using (NAudio.CoreAudioApi.MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
                {
                    AutoResetEvent needToReset = new AutoResetEvent(false);
                    NotificationClient notificationClient = new NotificationClient(ref needToReset);
                    enumerator.RegisterEndpointNotificationCallback(notificationClient);

                    while (!needToStop.WaitOne(100, false))
                    {
                        try
                        {
                            if (waveIn == null)
                            {
                                waveIn = new WasapiLoopbackCapture();
                                sampleRate = waveIn.WaveFormat.SampleRate;
                                channels = waveIn.WaveFormat.Channels;
                                bitsPerSample = waveIn.WaveFormat.BitsPerSample;
                                waveIn.DataAvailable += WaveIn_DataAvailable;
                                waveIn.StartRecording();
                            }
                            else
                            {
                                if (needToStop.WaitOne(100))
                                    break;
                            }
                        }
                        catch
                        {
                            needToStop.WaitOne(1000);
                            break;
                        }

                        if (needToReset.WaitOne(0, false) || needToStop.WaitOne(1))
                            break;
                    }

                    if (waveIn != null)
                    {
                        try
                        {
                            waveIn.StopRecording();
                            waveIn.Dispose();
                            waveIn.DataAvailable -= WaveIn_DataAvailable;
                            waveIn = null;
                        }
                        catch { }
                    }

                    enumerator.UnregisterEndpointNotificationCallback(notificationClient);
                    needToReset?.Dispose();
                }
            }
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            if ((e?.BytesRecorded ?? 0) > 0)
            {
                int samples = e.BytesRecorded / ((bitsPerSample + 7) / 8) / channels;

                IntPtr convertedSamples = Marshal.AllocHGlobal(e.BytesRecorded / 2);
                unsafe
                {
                    fixed (void* pBuffer = e.Buffer)
                    {
                        // FLTP to S16 변환 (추후에 오디오 관련 처리를 간편하게 하기 위해..)
                        float* src = (float*)pBuffer;
                        short* dest = (short*)convertedSamples.ToPointer();
                        for (int i = 0; i < e.BytesRecorded; i += 4)
                        {
                            *(dest++) = (short)(*(src++) * 32767.0f);
                        }

                        NewAudioPacketEventArgs eventArgs = new NewAudioPacketEventArgs(sampleRate, channels, MediaEncoder.SampleFormat.S16, samples, convertedSamples);
                        NewAudioPacket?.Invoke(this, eventArgs);
                    }
                }
                Marshal.FreeHGlobal(convertedSamples);
            }
        }

        public void Dispose()
        {
            if (needToStop != null)
            {
                needToStop.Set();
            }
            if (workerThread != null)
            {
                if (workerThread.IsAlive && !workerThread.Join(10000))
                    workerThread.Abort();
                workerThread = null;

                if (needToStop != null)
                    needToStop.Close();
                needToStop = null;
            }
        }
    }
}
