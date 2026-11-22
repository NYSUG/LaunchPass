using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LaunchPass.Catalog
{
    public interface IArcadeAudioDeck : IDisposable
    {
        event Action Ended;
        event Action Failed;
        Task OpenAsync(string track,double position,CancellationToken cancellation);
        double Position { get; }
        double Duration { get; }
        bool Playing { get; }
        double Volume { set; }
        void Play();
    }
    // UI-thread-affine controller, independent of WinRT. One deck for basic playback,
    // at most two for optional crossfade. Navigation does not recreate this owner.
    public sealed class ArcadeRadioController
    {
        private sealed class Deck { public string Track; public IArcadeAudioDeck Audio; public bool Ready, Broken; }
        private readonly Func<IArcadeAudioDeck> create;
        private readonly Func<double> now;
        private readonly ArcadeShuffleBag shuffle;
        private HashSet<string> library=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Deck current,incoming;
        private ArcadeMusicSettings settings=new ArcadeMusicSettings();
        private CancellationTokenSource operation;
        private bool busy,advancePending,resumePending;
        private int epoch;
        private double crossStarted=-1,masterFade=1;
        private string bookmark;
        private double bookmarkPosition;
        public bool Active { get; private set; }
        public bool CrossfadeFailed { get; private set; }
        public string CurrentTrack => current?.Track;
        public int DeckCount => (current==null?0:1)+(incoming==null?0:1);
        public event Action<string> Diagnostic;
        public ArcadeRadioController(Func<IArcadeAudioDeck> create,Func<double> now,Random random=null)
        { this.create=create;this.now=now;shuffle=new ArcadeShuffleBag(random); }
        public void Configure(ArcadeMusicSettings value)
        { settings=value??new ArcadeMusicSettings();if(!settings.Enabled)Suspend();else UpdateVolumes(); }
        public void SetLibrary(IEnumerable<string> tracks)
        { library=new HashSet<string>(tracks,StringComparer.OrdinalIgnoreCase);shuffle.SetItems(library); }
        public async Task ActivateAsync()
        {
            if(!settings.Enabled)return;
            Active=true;masterFade=1;
            if(current!=null)return;
            if(busy){resumePending=true;advancePending=true;return;}
            await AdvanceAsync(true);
        }
        private Deck NewDeck(string track)
        {
            var result=new Deck{Track=track,Audio=create()};
            result.Audio.Ended+=()=>Ended(result);
            result.Audio.Failed+=()=>Failed(result);
            result.Audio.Volume=0;return result;
        }
        private async void Ended(Deck deck)
        {
            if(!Active || !ReferenceEquals(deck,current))return;
            try { await NextAsync(); } catch(Exception ex){Diagnostic?.Invoke("ended: "+ex.Message);}
        }
        private async void Failed(Deck deck)
        {
            if(!Active || (!ReferenceEquals(deck,current)&&!ReferenceEquals(deck,incoming)))return;
            deck.Broken=true;
            shuffle.Exclude(deck.Track);
            if(ReferenceEquals(deck,incoming))
            {
                CrossfadeFailed=true;
                if(!busy){Dispose(ref incoming);crossStarted=-1;UpdateVolumes();}
                Diagnostic?.Invoke("crossfade disabled after media failure");
                return;
            }
            if(busy && !deck.Ready)return;
            try { await NextAsync(); } catch(Exception ex){Diagnostic?.Invoke("failed: "+ex.Message);}
        }
        public Task NextAsync() => AdvanceAsync(false);
        private async Task AdvanceAsync(bool resume)
        {
            if(!Active || !settings.Enabled)return;
            if(busy){advancePending=true;return;}
            busy=true;int version=epoch;var cancellation=new CancellationTokenSource();operation=cancellation;
            try
            {
                if(incoming!=null && incoming.Ready){Promote();return;}
                Dispose(ref current);Dispose(ref incoming);crossStarted=-1;
                for(int attempt=0;attempt<library.Count && Active && version==epoch;attempt++)
                {
                    string track=resume && bookmark!=null && library.Contains(bookmark)?bookmark:shuffle.Next();
                    double position=resume && track==bookmark?bookmarkPosition:0;
                    resume=false;bookmark=null;
                    if(track==null)return;
                    var deck=NewDeck(track);current=deck;
                    try
                    {
                        await deck.Audio.OpenAsync(track,position,cancellation.Token);
                        if(deck.Broken)throw new InvalidOperationException("Media failed during opening.");
                        if(!Active || version!=epoch || !ReferenceEquals(current,deck))return;
                        deck.Ready=true;UpdateVolumes();deck.Audio.Play();return;
                    }
                    catch(OperationCanceledException){return;}
                    catch(Exception){shuffle.Exclude(track);Dispose(ref current);Diagnostic?.Invoke("skipped unreadable MP3: "+track);}
                }
            }
            finally
            {
                cancellation.Dispose();if(ReferenceEquals(operation,cancellation))operation=null;busy=false;
                bool again=advancePending,restore=resumePending;advancePending=resumePending=false;
                if(again && Active)await AdvanceAsync(restore);
            }
        }
        public async Task TickAsync()
        {
            if(!Active || busy || current==null)return;
            if(incoming!=null && incoming.Ready)
            {
                double remaining=current.Audio.Duration-current.Audio.Position;
                if(crossStarted<0 && remaining<=settings.Crossfade.Seconds)
                { try{incoming.Audio.Play();crossStarted=now();}catch{CrossfadeFailed=true;Dispose(ref incoming);Diagnostic?.Invoke("crossfade play failed; sequential fallback");return;} }
                if(crossStarted>=0)
                {
                    if(now()-crossStarted>=settings.Crossfade.Seconds*1000){Promote();return;}
                    UpdateVolumes();
                }
                return;
            }
            if(!settings.Crossfade.Enabled || CrossfadeFailed || shuffle.Count<2)return;
            double duration=current.Audio.Duration,left=duration-current.Audio.Position;
            if(duration<=0 || double.IsNaN(duration) || double.IsInfinity(duration) || left>settings.Crossfade.Seconds+1 || left<=0)return;
            busy=true;int version=epoch;var cancellation=new CancellationTokenSource();operation=cancellation;
            try
            {
                string track=shuffle.Next();if(track==null)return;
                var deck=NewDeck(track);incoming=deck;
                await deck.Audio.OpenAsync(track,0,cancellation.Token);
                if(deck.Broken)throw new InvalidOperationException("Media failed during opening.");
                if(!Active || version!=epoch || !ReferenceEquals(incoming,deck))return;
                deck.Ready=true;
            }
            catch(OperationCanceledException){}
            catch(Exception){if(incoming!=null)shuffle.Exclude(incoming.Track);Dispose(ref incoming);crossStarted=-1;CrossfadeFailed=true;UpdateVolumes();Diagnostic?.Invoke("crossfade unavailable; sequential fallback");}
            finally
            {
                cancellation.Dispose();if(ReferenceEquals(operation,cancellation))operation=null;busy=false;
                bool again=advancePending,restore=resumePending;advancePending=resumePending=false;if(again && Active)await AdvanceAsync(restore);
            }
        }
        private void Promote()
        {
            Dispose(ref current);current=incoming;incoming=null;crossStarted=-1;
            UpdateVolumes();if(current!=null && !current.Audio.Playing)current.Audio.Play();
        }
        public static double Blend(double elapsed,double duration) => duration<=0?1:Math.Max(0,Math.Min(1,elapsed/duration));
        private void UpdateVolumes()
        {
            double level=settings.Muted?0:settings.Volume*masterFade;
            double mix=crossStarted<0?0:Blend(now()-crossStarted,settings.Crossfade.Seconds*1000);
            if(current!=null)current.Audio.Volume=level*(1-mix);
            if(incoming!=null)incoming.Audio.Volume=level*mix;
        }
        public async Task FadeOutAndSuspendAsync()
        {
            int version=epoch;
            try
            {
                if(Active && current!=null && !settings.Muted)
                    for(int step=1;step<=6 && version==epoch;step++){masterFade=1-step/6.0;UpdateVolumes();await Task.Delay(50);}
            }
            finally { if(version==epoch)Suspend(); }
        }
        public void Suspend()
        {
            var save=crossStarted>=0 && Blend(now()-crossStarted,settings.Crossfade.Seconds*1000)>=.5?incoming:current;
            if(save!=null && save.Ready){bookmark=save.Track;try{bookmarkPosition=Math.Max(0,save.Audio.Position);}catch{bookmarkPosition=0;}}
            Active=false;epoch++;operation?.Cancel();Dispose(ref current);Dispose(ref incoming);crossStarted=-1;masterFade=1;
        }
        private static void Dispose(ref Deck deck){var old=deck;deck=null;if(old!=null){try{old.Audio.Dispose();}catch{}}}
    }
}
