﻿using LibVLCSharp.Shared;
using Manager.Shared;
using Manager.Shared.Delegates;
using Manager.Shared.Entities;
using Manager.Shared.Enums;
using Manager.Shared.Interfaces;

namespace Manager.Services.VlcVideo;

public class LibVlcVideoBackendService : ManagerComponent, ISingleVideoView, 
    IVideoBackendService, IAudioBackendService, ISubtitleBackendService,
    IBackendSupportsVideoStreamSelection, IBackendSupportsAudioStreamSelection, IBackendSupportsSubtitleStreamSelection
{
    public event AudioServiceGlobalDeviceChangedEventHandler? GlobalDeviceChanged;
    public event AudioServiceChannelVolumeChangedEventHandler? ChannelVolumeChanged;

    public event BackendServiceChannelCreatedEventHandler? ChannelCreated;
    public event BackendServiceChannelDestroyedEventHandler? ChannelDestroyed;
    public event BackendServiceChannelStateChangedEventHandler? ChannelStateChanged;
    public event BackendServiceChannelPositionChangedEventHandler? ChannelPositionChanged;

    public LibVLC? LibVlc { get; private set; }
    public MediaPlayer? MediaPlayer { get; private set; }
    
    public LibVlcVideoBackendService(string name, ulong parent) : base(name, parent)
    {
    }

    public override ValueTask<bool> InitializeAsync(params string[] options)
    {
        this.LibVlc = new LibVLC(options);
        this.MediaPlayer = new MediaPlayer(this.LibVlc);
        this.MediaPlayer.EncounteredError += (sender, args) =>
        {
            this.SendError(this, nameof(this.InitializeAsync), $"MediaPlayer encountered error {args}", args);
            Console.WriteLine($"MediaPlayer encountered error {args}: {this.LibVlc.LastLibVLCError}");
        };
        this.Initialized = true;
        return ValueTask.FromResult(true);
    }

    public ValueTask<AudioDevice[]?> GetDevicesAsync()
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetDevicesAsync), "Service not initialized");
            return ValueTask.FromResult(default(AudioDevice[]));
        }
        
        var deviceCount = this.MediaPlayer?.AudioOutputDeviceEnum.Length;
        if (deviceCount == null)
        {
            this.SendError(this, nameof(GetDevicesAsync), "No devices found");
            return ValueTask.FromResult(default(AudioDevice[]));
        }
        
        var devices = new AudioDevice[deviceCount.Value];
        for (var i = 0; i < deviceCount.Value; i++)
        {
            var device = this.MediaPlayer?.AudioOutputDeviceEnum[i];
            if (device == null)
                continue;
            devices[i] = new AudioDevice
            {
                AssociatedBackend = this,
                Id = device.Value.DeviceIdentifier,
                Name = device.Value.Description,
            };
        }

        return ValueTask.FromResult((AudioDevice[]?)devices);
    }

    public ValueTask<AudioDevice?> GetCurrentlySelectedDeviceAsync()
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetCurrentlySelectedDeviceAsync), "Service not initialized");
            return ValueTask.FromResult(default(AudioDevice));
        }

        var device = this.MediaPlayer?.OutputDevice;
        if (device == null)
        {
            this.SendError(this, nameof(GetCurrentlySelectedDeviceAsync), "No device selected");
            return ValueTask.FromResult(default(AudioDevice));
        }

        var devices = this.MediaPlayer?.AudioOutputDeviceEnum.FirstOrDefault(x => x.DeviceIdentifier == device);
        if (devices == null)
        {
            this.SendError(this, nameof(GetCurrentlySelectedDeviceAsync), $"Device {device} not found");
            return ValueTask.FromResult(default(AudioDevice));
        }

        return ValueTask.FromResult((AudioDevice?)new AudioDevice
        {
            AssociatedBackend = this,
            Id = devices.Value.DeviceIdentifier,
            Name = devices.Value.Description,
        });
    }

    public ValueTask<bool> SetDeviceAsync(AudioDevice device)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(SetDeviceAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }

        var devices = this.MediaPlayer?.AudioOutputDeviceEnum.FirstOrDefault(x => x.DeviceIdentifier == device.Id);
        if (devices == null)
        {
            this.SendError(this, nameof(SetDeviceAsync), $"Device {device.Name} not found");
            return ValueTask.FromResult(false);
        }

        this.MediaPlayer!.SetOutputDevice(device.Id);
        this.GlobalDeviceChanged?.Invoke(this, device);
        return ValueTask.FromResult(true);
    }

    public async ValueTask<IMediaChannel?> CreateChannelAsync(PlayItem playItem, Action<PlayItem>? onEnded = null)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(CreateChannelAsync), "Service not initialized");
            return default;
        }

        if (playItem.CacheState == CacheState.NotCached)
        {
            this.SendError(this, nameof(CreateChannelAsync), $"PlayItem {playItem.Title} is not cached");
            return default;
        }
        
        Media media;
        if (playItem.CacheState is CacheState.Memory)
        {
            var cacheStream = await playItem.CacheStrategy.GetCachedStreamAsync(playItem);
            if (cacheStream == null)
            {
                this.SendError(this, nameof(CreateChannelAsync), $"Failed to get cached stream for {playItem.Title}");
                return default;
            }
            var mediaInput = new StreamMediaInput(cacheStream);
            media = new Media(this.LibVlc!, mediaInput);
        }
        else
        {
            var cachePath = await playItem.CacheStrategy.GetCachedPathAsync(playItem);
            if (cachePath == null)
            {
                this.SendError(this, nameof(CreateChannelAsync), $"Failed to get cached path for {playItem.Title}");
                return default;
            }
            media = new Media(this.LibVlc!, cachePath);
        }
        media = new Media(this.LibVlc!, playItem.OwnerPath);
        var status = await media.Parse();
        if (status == MediaParsedStatus.Failed)
        {
            this.SendError(this, nameof(CreateChannelAsync), $"Failed to parse media {playItem.Title}");
            return default;
        }

        if (onEnded != null)
        {
            this.MediaPlayer!.EndReached += OnEndReached;
            void OnEndReached(object? sender, EventArgs e)
            {
                onEnded(playItem);
                this.MediaPlayer!.EndReached -= OnEndReached;
            }   
        }
        
        var channel = new LibVlcChannel(this, playItem, media);
        this.ChannelCreated?.Invoke(this, channel);
        return channel;
    }

    public ValueTask<bool> DestroyChannelAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(DestroyChannelAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(DestroyChannelAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(DestroyChannelAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        if (Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia)) //Equals to compare value, not reference
        {
            this.MediaPlayer!.Stop();
            this.MediaPlayer!.Media = null;
        }
        
        libVlcChannel.LibVlcMedia.Dispose();
        this.ChannelDestroyed?.Invoke(this, libVlcChannel);
        return ValueTask.FromResult(true);
    }

    public async ValueTask<bool> PlayChannelAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(PlayChannelAsync), "Service not initialized");
            return false;
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(PlayChannelAsync), "Channel is not a LibVlcChannel");
            return false;
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(PlayChannelAsync), "Channel is not associated with this backend");
            return false;
        }
        //TODO: Check if channel is already playing a different media and stop it
        
        var tcs = new TaskCompletionSource();
        this.MediaPlayer!.Playing += OnPlaying;

        void OnPlaying(object? sender, EventArgs e)
        {
            tcs.SetResult();
            this.MediaPlayer!.Playing -= OnPlaying;
        }

        this.MediaPlayer!.Media = libVlcChannel.LibVlcMedia;
        var couldPlay = this.MediaPlayer!.Play();
        if (!couldPlay)
        {
            this.SendError(this, nameof(PlayChannelAsync), "Failed to play media");
            return false;
        }
        
        await tcs.Task;
        var convertedVolume = (int)(libVlcChannel.ChannelVolume * 100);
        if ((int)libVlcChannel.ChannelVolume != this.MediaPlayer!.Volume)
        {
            this.MediaPlayer!.Volume = convertedVolume;
        }
        var couldVTrack = this.MediaPlayer!.SetVideoTrack(libVlcChannel.VideoStreamIndex);
        var couldATrack = this.MediaPlayer!.SetAudioTrack(libVlcChannel.AudioStreamIndex);
        var couldSTrack = this.MediaPlayer!.SetSpu(libVlcChannel.SubtitleStreamIndex);
        
        this.ChannelStateChanged?.Invoke(this, libVlcChannel, ChannelState.Playing);
        return true;
    }

    public ValueTask<bool> PauseChannelAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(PauseChannelAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(PauseChannelAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(PauseChannelAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(PauseChannelAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult(false);
        }
        
        this.MediaPlayer!.SetPause(true);
        var state = this.MediaPlayer!.State;
        if (state != VLCState.Paused)
        {
            this.SendError(this, nameof(this.PauseChannelAsync), $"Unexpected state {state}");
            return ValueTask.FromResult(false);
        }

        this.ChannelStateChanged?.Invoke(this, libVlcChannel, ChannelState.Paused);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> ResumeChannelAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(ResumeChannelAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(ResumeChannelAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(ResumeChannelAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(ResumeChannelAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult(false);
        }
        
        this.MediaPlayer!.SetPause(false);
        var state = this.MediaPlayer!.State;
        if (state != VLCState.Playing)
        {
            this.SendError(this, nameof(this.ResumeChannelAsync), $"Unexpected state {state}");
            return ValueTask.FromResult(false);
        }
        
        this.ChannelStateChanged?.Invoke(this, libVlcChannel, ChannelState.Playing);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> StopChannelAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(StopChannelAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(StopChannelAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(StopChannelAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(StopChannelAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult(false);
        }
        
        this.MediaPlayer!.Stop();
        var state = this.MediaPlayer!.State;
        if (state != VLCState.Stopped)
        {
            this.SendError(this, nameof(this.StopChannelAsync), $"Unexpected state {state}");
            return ValueTask.FromResult(false);
        }
        
        this.ChannelStateChanged?.Invoke(this, libVlcChannel, ChannelState.Stopped);
        return ValueTask.FromResult(true);
    }

    public ValueTask<ChannelState?> GetChannelStateAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetChannelStateAsync), "Service not initialized");
            return ValueTask.FromResult((ChannelState?)default(ChannelState));
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetChannelStateAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult((ChannelState?)default(ChannelState));
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(GetChannelStateAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult((ChannelState?)default(ChannelState));
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(GetChannelStateAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult((ChannelState?)default(ChannelState));
        }
        
        //Very eh but should work
        var state = this.MediaPlayer!.State;
        ChannelState? channelState = null;
        if (state == VLCState.Playing)
            channelState = ChannelState.Playing;
        else if (state == VLCState.Paused)
            channelState = ChannelState.Paused;
        else if (state == VLCState.Stopped)
            channelState = ChannelState.Stopped;
        else
            this.SendError(this, nameof(this.GetChannelStateAsync), $"Unexpected state {state}");

        return ValueTask.FromResult(channelState);
    }

    public async ValueTask<bool> SetChannelStateAsync(IMediaChannel channel, ChannelState state)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(SetChannelStateAsync), "Service not initialized");
            return false;
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(SetChannelStateAsync), "Channel is not a LibVlcChannel");
            return false;
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(SetChannelStateAsync), "Channel is not associated with this backend");
            return false;
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(SetChannelStateAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return false;
        }
        
        switch (state)
        {
            case ChannelState.Playing:
                return await this.PlayChannelAsync(channel);
            case ChannelState.Paused:
                return await this.PauseChannelAsync(channel);
            case ChannelState.Stopped:
                return await this.StopChannelAsync(channel);
            default:
                this.SendError(this, nameof(this.SetChannelStateAsync), $"Unexpected state {state}");
                return false;
        }
    }

    public ValueTask<TimeSpan?> GetChannelPositionAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetChannelPositionAsync), "Service not initialized");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetChannelPositionAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(GetChannelPositionAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(GetChannelPositionAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        
        var position = this.MediaPlayer!.Time;
        if (position == -1)
        {
            this.SendError(this, nameof(this.GetChannelPositionAsync), $"Unexpected position {position}, not media is playing");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        
        return ValueTask.FromResult((TimeSpan?)TimeSpan.FromMilliseconds(position));
    }

    public ValueTask<bool> SetChannelPositionAsync(IMediaChannel channel, TimeSpan position)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(SetChannelPositionAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(SetChannelPositionAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(SetChannelPositionAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(SetChannelPositionAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult(false);
        }

        this.MediaPlayer!.SeekTo(position);
        this.ChannelPositionChanged?.Invoke(this, libVlcChannel, position);
        return ValueTask.FromResult(true);
    }
    
    public ValueTask<bool> SetChannelPositionAsync(IMediaChannel channel, double positionMs)
        => this.SetChannelPositionAsync(channel, TimeSpan.FromMilliseconds(positionMs));

    public ValueTask<TimeSpan?> GetChannelLengthAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetChannelLengthAsync), "Service not initialized");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetChannelLengthAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(GetChannelLengthAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult((TimeSpan?)default(TimeSpan));
        }
        
        return libVlcChannel.LibVlcMedia.Duration == -1
            ? ValueTask.FromResult((TimeSpan?)default(TimeSpan))
            : ValueTask.FromResult((TimeSpan?)TimeSpan.FromMilliseconds(libVlcChannel.LibVlcMedia.Duration));
    }

    public ValueTask<AudioDevice?> GetChannelDeviceAsync(IMediaChannel channel)
    {
        return this.GetCurrentlySelectedDeviceAsync();
    }

    public ValueTask<bool> SetChannelDeviceAsync(IMediaChannel channel, AudioDevice device)
    {
        return this.SetDeviceAsync(device);
    }

    public ValueTask<float?> GetChannelVolumeAsync(IMediaChannel channel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetChannelVolumeAsync), "Service not initialized");
            return ValueTask.FromResult((float?)default(float));
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetChannelVolumeAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult((float?)default(float));
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(GetChannelVolumeAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult((float?)default(float));
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(GetChannelVolumeAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult((float?)default(float));
        }
        
        var volume = this.MediaPlayer!.Volume;
        if (volume == -1)
        {
            this.SendError(this, nameof(this.GetChannelVolumeAsync), $"Unexpected volume {volume}");
            return ValueTask.FromResult((float?)default(float));
        }
        var volumePercent = volume / 100f;
        return ValueTask.FromResult((float?)volumePercent);
    }

    public ValueTask<bool> SetChannelVolumeAsync(IMediaChannel channel, float volume)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(SetChannelVolumeAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (channel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(SetChannelVolumeAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedAudioBackend != this && libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this)
        {
            this.SendError(this, nameof(SetChannelVolumeAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia))
        {
            this.SendError(this, nameof(SetChannelVolumeAsync), "LibVlcChannel is not active on this backends MediaPlayer");
            return ValueTask.FromResult(false);
        }
        
        var volumePercent = volume * 100;
        this.MediaPlayer!.Volume = (int)volumePercent;
        this.ChannelVolumeChanged?.Invoke(this, libVlcChannel, volume);
        return ValueTask.FromResult(true);
    }
    
    public ValueTask<SelectableMediaStream[]?> GetSelectableVideoStreamsAsync(IVideoChannel mediaChannel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetSelectableVideoStreamsAsync), "Service not initialized");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        if (mediaChannel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetSelectableVideoStreamsAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        if (libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this && libVlcChannel.AssociatedAudioBackend != this)
        {
            this.SendError(this, nameof(GetSelectableVideoStreamsAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }

        var videoStreams = new List<SelectableMediaStream>();
        foreach (var videoStream in libVlcChannel.LibVlcMedia.Tracks.Where(x => x.TrackType == TrackType.Video))
        {
            var selectableVideoStream = new SelectableMediaStream
            {
                Identifier = videoStream.Id.ToString(),
                Name = $"[{videoStream.Language}] {videoStream.Description}",
            };
            videoStreams.Add(selectableVideoStream);
        }
        return ValueTask.FromResult((SelectableMediaStream[]?)videoStreams.ToArray());
    }

    public ValueTask<bool> SetSelectedVideoStreamAsync(IVideoChannel mediaChannel, SelectableMediaStream? stream)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetSelectableVideoStreamsAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (mediaChannel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetSelectableVideoStreamsAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this && libVlcChannel.AssociatedAudioBackend != this)
        {
            this.SendError(this, nameof(GetSelectableVideoStreamsAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        
        int identifier;
        if (stream == null)
            identifier = -1;
        else
            identifier = int.Parse(stream.Identifier);
        
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia)){
            libVlcChannel.VideoStreamIndex = identifier;
            return ValueTask.FromResult(true);
        }
        
        var couldSet = this.MediaPlayer!.SetVideoTrack(identifier);
        if (!couldSet)
        {
            this.SendError(this, nameof(SetSelectedVideoStreamAsync), "Failed to set video stream");
            return ValueTask.FromResult(false);
        }
        libVlcChannel.VideoStreamIndex = identifier;
        return ValueTask.FromResult(true);
    }

    public ValueTask<SelectableMediaStream[]?> GetSelectableAudioStreamsAsync(IAudioChannel mediaChannel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetSelectableAudioStreamsAsync), "Service not initialized");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        if (mediaChannel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetSelectableAudioStreamsAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        if (libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this && libVlcChannel.AssociatedAudioBackend != this)
        {
            this.SendError(this, nameof(GetSelectableAudioStreamsAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        
        var audioStreams = new List<SelectableMediaStream>();
        foreach (var audioStream in libVlcChannel.LibVlcMedia.Tracks.Where(x => x.TrackType == TrackType.Audio))
        {
            var selectableAudioStream = new SelectableMediaStream
            {
                Identifier = audioStream.Id.ToString(),
                Name = $"[{audioStream.Language}] {audioStream.Description}",
            };
            audioStreams.Add(selectableAudioStream);
        }
        return ValueTask.FromResult((SelectableMediaStream[]?)audioStreams.ToArray());
    }

    public ValueTask<bool> SetSelectedAudioStreamAsync(IAudioChannel mediaChannel, SelectableMediaStream? stream)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(SetSelectedAudioStreamAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (mediaChannel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(SetSelectedAudioStreamAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this && libVlcChannel.AssociatedAudioBackend != this)
        {
            this.SendError(this, nameof(SetSelectedAudioStreamAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        
        int identifier;
        if (stream == null)
            identifier = -1;
        else
            identifier = int.Parse(stream.Identifier);
        
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia)){
            libVlcChannel.AudioStreamIndex = identifier;
            return ValueTask.FromResult(true);
        }
        
        var couldSet = this.MediaPlayer!.SetAudioTrack(identifier);
        if (!couldSet)
        {
            this.SendError(this, nameof(SetSelectedAudioStreamAsync), "Failed to set audio stream");
            return ValueTask.FromResult(false);
        }
        
        libVlcChannel.AudioStreamIndex = identifier;
        return ValueTask.FromResult(true);
    }

    public ValueTask<SelectableMediaStream[]?> GetSelectableSubtitleStreamsAsync(ISubtitleChannel mediaChannel)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(GetSelectableSubtitleStreamsAsync), "Service not initialized");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        if (mediaChannel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(GetSelectableSubtitleStreamsAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        if (libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this && libVlcChannel.AssociatedAudioBackend != this)
        {
            this.SendError(this, nameof(GetSelectableSubtitleStreamsAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult((SelectableMediaStream[]?)default(SelectableMediaStream[]));
        }
        
        var subtitleStreams = new List<SelectableMediaStream>();
        foreach (var subtitleStream in libVlcChannel.LibVlcMedia.Tracks.Where(x => x.TrackType == TrackType.Text))
        {
            var selectableSubtitleStream = new SelectableMediaStream
            {
                Identifier = subtitleStream.Id.ToString(),
                Name = $"[{subtitleStream.Language}] {subtitleStream.Description}",
            };
            subtitleStreams.Add(selectableSubtitleStream);
        }
        return ValueTask.FromResult((SelectableMediaStream[]?)subtitleStreams.ToArray());
    }

    public ValueTask<bool> SetSelectedSubtitleStreamAsync(ISubtitleChannel mediaChannel, SelectableMediaStream? stream)
    {
        if (!this.Initialized)
        {
            this.SendError(this, nameof(SetSelectedSubtitleStreamAsync), "Service not initialized");
            return ValueTask.FromResult(false);
        }
        if (mediaChannel is not LibVlcChannel libVlcChannel)
        {
            this.SendError(this, nameof(SetSelectedSubtitleStreamAsync), "Channel is not a LibVlcChannel");
            return ValueTask.FromResult(false);
        }
        if (libVlcChannel.AssociatedVideoBackend != this && libVlcChannel.AssociatedSubtitleBackend != this && libVlcChannel.AssociatedAudioBackend != this)
        {
            this.SendError(this, nameof(SetSelectedSubtitleStreamAsync), "Channel is not associated with this backend");
            return ValueTask.FromResult(false);
        }
        
        int identifier;
        if (stream == null)
            identifier = -1;
        else
            identifier = int.Parse(stream.Identifier);
        
        if (!Equals(this.MediaPlayer!.Media, libVlcChannel.LibVlcMedia)){
            libVlcChannel.SubtitleStreamIndex = identifier;
            return ValueTask.FromResult(true);
        }
        
        var couldSet = this.MediaPlayer!.SetSpu(identifier);
        if (!couldSet)
        {
            this.SendError(this, nameof(SetSelectedSubtitleStreamAsync), "Failed to set subtitle stream");
            return ValueTask.FromResult(false);
        }
        
        libVlcChannel.SubtitleStreamIndex = identifier;
        return ValueTask.FromResult(true);
    }

    static LibVlcVideoBackendService()
    {
        Core.Initialize();
    }
}