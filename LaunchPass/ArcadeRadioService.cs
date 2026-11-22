using LaunchPass.Catalog;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI.Core;

namespace RetroPass
{
    internal sealed class ArcadeMediaDeck : IArcadeAudioDeck
    {
        private readonly CoreDispatcher dispatcher;
        private readonly MediaPlayer player;
        private MediaSource source;
        private TaskCompletionSource<bool> opened;
        private bool disposed;
        private double seek;
        public event Action Ended,Failed;
        internal ArcadeMediaDeck(CoreDispatcher dispatcher)
        {
            this.dispatcher=dispatcher;
            player=new MediaPlayer{AutoPlay=false,AudioCategory=MediaPlayerAudioCategory.GameMedia};
            player.CommandManager.IsEnabled=false;
            player.MediaOpened+=Opened;player.MediaEnded+=MediaEnded;player.MediaFailed+=MediaFailed;
        }
        public async Task OpenAsync(string track,double position,CancellationToken cancellation)
        {
            opened=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);seek=position;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using(timeout.Token.Register(()=>opened.TrySetCanceled()))
                {
                    var folder=await ApplicationData.Current.LocalFolder.GetFolderAsync("Music").AsTask(timeout.Token);
                    var file=await folder.GetFileAsync(track).AsTask(timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    source=MediaSource.CreateFromStorageFile(file);player.Source=source;
                    await opened.Task;
                }
            }
        }
        private async void Dispatch(Action action)
        {
            try{if(dispatcher.HasThreadAccess){if(!disposed)action();}else await dispatcher.RunAsync(CoreDispatcherPriority.Normal,()=>{if(!disposed)action();});}
            catch(Exception ex){StartupLog.Write("Radio callback: "+ex.Message);}
        }
        private void Opened(MediaPlayer sender,object args)=>Dispatch(()=>
        {
            try
            {
                if(seek>0 && player.PlaybackSession.CanSeek)
                {
                    double duration=player.PlaybackSession.NaturalDuration.TotalSeconds;
                    player.PlaybackSession.Position=TimeSpan.FromSeconds(duration>0?Math.Min(seek,Math.Max(0,duration-.1)):seek);
                }
                opened.TrySetResult(true);
            }
            catch(Exception ex){opened.TrySetException(ex);}
        });
        private void MediaEnded(MediaPlayer sender,object args)=>Dispatch(()=>Ended?.Invoke());
        private void MediaFailed(MediaPlayer sender,MediaPlayerFailedEventArgs args)=>Dispatch(()=>
        {
            if(opened!=null && !opened.Task.IsCompleted)opened.TrySetException(new InvalidOperationException("Unsupported or unreadable MP3."));
            else Failed?.Invoke();
        });
        public double Position=>disposed?0:player.PlaybackSession.Position.TotalSeconds;
        public double Duration=>disposed?0:player.PlaybackSession.NaturalDuration.TotalSeconds;
        public bool Playing=>!disposed && player.PlaybackSession.PlaybackState==MediaPlaybackState.Playing;
        public double Volume{set{if(!disposed)player.Volume=Math.Max(0,Math.Min(1,value));}}
        public void Play(){if(!disposed)player.Play();}
        public void Dispose()
        {
            if(disposed)return;disposed=true;opened?.TrySetCanceled();
            player.MediaOpened-=Opened;player.MediaEnded-=MediaEnded;player.MediaFailed-=MediaFailed;
            player.Pause();player.Source=null;source?.Dispose();source=null;player.Dispose();
        }
    }
}
