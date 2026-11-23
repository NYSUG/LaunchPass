using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LaunchPass.Catalog;

class Program
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
    sealed class Deck:IArcadeAudioDeck
    {
        public event Action Ended,Failed;
        public double Position{get;set;} public double Duration{get;set;}=100;
        public bool Playing{get;private set;} public double Volume{get;set;}
        public string Track; public bool Disposed;public bool Corrupt;
        public TaskCompletionSource<bool> Pending;
        public async Task OpenAsync(string track,double position,CancellationToken token)
        {Track=track;Position=position;if(Pending!=null)await Pending.Task.WaitAsync(token);if(Corrupt)throw new InvalidOperationException("bad mp3");}
        public void Play(){if(Disposed)throw new Exception("disposed playback");Playing=true;}
        public void Dispose(){Disposed=true;Playing=false;}
        public void End(){Playing=false;Ended?.Invoke();}
        public void Fail(){Failed?.Invoke();}
    }
    static async Task Main()
    {
        var defaults=ArcadeExperienceSettings.Parse("{\"version\":1}");
        var reported = ArcadeExperienceSettings.Parse("{\"version\":1,\"music\":{\"enabled\":true,\"volume\":0.5},\"attract\":{\"enabled\":false,\"idleSeconds\":180,\"cycleSeconds\":12}}");
        Check(reported.Music.Enabled && reported.Music.Volume == .5 && !reported.Attract.Enabled, "reported Xbox settings explicitly disable museum");
        CabinetAction next; int token;
        Check(!CabinetInputMap.Defaults().TryResolve(78, out token, out next), "NextTrack has no default physical binding");
        var nextMap = CabinetInputMap.Parse("{\"version\":1,\"bindings\":[{\"key\":\"N\",\"action\":\"NextTrack\"}]}");
        Check(nextMap.TryResolve(78, out token, out next) && next == CabinetAction.NextTrack, "NextTrack accepts explicit configurable binding");
        Check(!defaults.Music.Enabled&&!defaults.Attract.Enabled&&defaults.Branding.Enabled&&defaults.Splash.Enabled,"safe independent defaults");
        Check(defaults.Music.Volume==.2&&!defaults.Music.Crossfade.Enabled&&defaults.Music.Crossfade.Seconds==3,"music safe defaults");
        var configured=ArcadeExperienceSettings.Parse("{\"version\":1,\"music\":{\"enabled\":true,\"volume\":0.4,\"muted\":true,\"crossfade\":{\"enabled\":true}},\"branding\":{\"enabled\":false},\"attract\":{\"enabled\":true}}");
        Check(configured.Music.Enabled&&configured.Music.Muted&&configured.Music.Crossfade.Enabled&&!configured.Branding.Enabled&&configured.Attract.Enabled,"independent configuration");
        foreach(string text in new[]{"{}","[]","null","{\"version\":2}","{\"version\":1} {}","{\"version\":1,\"music\":{\"volume\":5}}","{\"version\":1,\"attract\":{\"idleSeconds\":0}}","{\"version\":1,\"music\":{\"crossfade\":{\"seconds\":8}}}","{\"version\":1,\"music\":{\"enabled\":\"yes\"}}"})
        {try{ArcadeExperienceSettings.Parse(text);throw new Exception("accepted bad settings "+text);}catch(FormatException){checks++;}}
        var bag=new ArcadeShuffleBag(new Random(123));bag.SetItems(new[]{"a","b","c","A"});string last=null;
        for(int cycle=0;cycle<100;cycle++){var tracks=new[]{bag.Next(),bag.Next(),bag.Next()};Check(tracks.Distinct().Count()==3,"shuffle exhausts before repeat");Check(tracks[0]!=last,"no immediate cycle-boundary repeat");last=tracks[2];}
        bag.Exclude("a");for(int i=0;i<10;i++)Check(bag.Next()!="a","bad track quarantined");
        bag.SetItems(Array.Empty<string>());Check(bag.Next()==null,"empty library");bag.SetItems(new[]{"one"});Check(bag.Next()=="one"&&bag.Next()=="one","one-track library supported");
        Console.WriteLine("Phase B settings/shuffle policy passed");
        double now=0;var decks=new List<Deck>();var music=new ArcadeMusicSettings{Enabled=true,Volume=.2};
        var radio=new ArcadeRadioController(()=>{var d=new Deck();decks.Add(d);return d;},()=>now,new Random(2));radio.Configure(music);radio.SetLibrary(new[]{"a","b","c"});await radio.ActivateAsync();
        Check(decks.Count==1&&decks[0].Playing&&decks[0].Volume==.2,"single-player basic playback");
        string first=radio.CurrentTrack;decks[0].Position=23;await radio.ActivateAsync();Check(decks.Count==1&&decks[0].Position==23,"normal UI activity does not recreate playback");
        radio.Suspend();Check(decks[0].Disposed&&radio.DeckCount==0,"release on background");await radio.ActivateAsync();Check(radio.CurrentTrack==first&&decks.Last().Position==23,"resume saved position");
        await radio.NextAsync();Check(radio.CurrentTrack!=first&&radio.DeckCount==1,"next track without duplicate sessions");
        var before=radio.CurrentTrack;decks.Last().End();await Task.Delay(5);Check(radio.CurrentTrack!=before,"automatic next at media end");
        music.Muted=true;radio.Configure(music);Check(decks.Last().Volume==0,"mute");radio.Suspend();decks[0].End();Check(!radio.Active&&radio.DeckCount==0,"late media callbacks cannot restart playback");
        var broken=new ArcadeRadioController(()=>new Deck{Corrupt=true},()=>now);broken.Configure(new ArcadeMusicSettings{Enabled=true});broken.SetLibrary(new[]{"bad1","bad2"});await broken.ActivateAsync();Check(broken.DeckCount==0,"all corrupt tracks settle silently");broken.Suspend();
        var pendingDeck=new Deck{Pending=new TaskCompletionSource<bool>()};var late=new ArcadeRadioController(()=>pendingDeck,()=>now);late.Configure(new ArcadeMusicSettings{Enabled=true});late.SetLibrary(new[]{"late"});var opening=late.ActivateAsync();late.Suspend();await opening;Check(!pendingDeck.Playing&&pendingDeck.Disposed,"suspend cancels an opening track");
        Console.WriteLine("Phase B playback lifecycle passed");
        decks.Clear();music=new ArcadeMusicSettings{Enabled=true,Volume=.2,Crossfade=new RadioCrossfadeSettings{Enabled=true,Seconds=3}};
        radio=new ArcadeRadioController(()=>{var d=new Deck();decks.Add(d);return d;},()=>now,new Random(3));radio.Configure(music);radio.SetLibrary(new[]{"a","b","c"});await radio.ActivateAsync();decks[0].Position=98;await radio.TickAsync();await radio.TickAsync();
        Check(radio.DeckCount==2&&decks[1].Playing,"optional second deck crossfade");now=1500;await radio.TickAsync();Check(Math.Abs(decks[0].Volume-.1)<.001&&Math.Abs(decks[1].Volume-.1)<.001,"bounded linear crossfade gains");now=3100;await radio.TickAsync();Check(radio.DeckCount==1&&decks[0].Disposed&&decks[1].Volume==.2,"crossfade releases old deck");
        await radio.FadeOutAndSuspendAsync();Check(!radio.Active&&radio.DeckCount==0,"fade stops all decks before launch");
        decks.Clear();radio=new ArcadeRadioController(()=>{var d=new Deck{Corrupt=decks.Count>0};decks.Add(d);return d;},()=>now);radio.Configure(music);radio.SetLibrary(new[]{"a","b","c"});await radio.ActivateAsync();decks[0].Position=98;await radio.TickAsync();Check(radio.CrossfadeFailed&&radio.DeckCount==1&&decks[0].Playing,"crossfade failure retains sequential player");radio.Suspend();
        Console.WriteLine("Phase C crossfade/fallback passed");
        var policy=new ArcadeSessionPolicy{Foreground=true,InternalFrontend=true,PremiumVisible=true};Check(policy.ShowSplash(true)&&!policy.CanAttract,"startup protected");policy.Ready=true;Check(!policy.ShowSplash(true)&&policy.CanAttract&&policy.CanPlayMusic,"ready browser");policy.Launching=true;Check(!policy.CanPlayMusic&&!policy.CanAttract,"launch overrides music and idle");policy.Launching=false;policy.Dialog=true;Check(!policy.CanAttract&&policy.CanPlayMusic,"dialogs retain music but suppress attract");policy.Dialog=false;policy.Failed=true;policy.Ready=false;Check(!policy.ShowSplash(true)&&!policy.CanAttract,"error cannot be hidden behind splash");
        Console.WriteLine("Phase D startup/session policy passed");
        var attract=new ArcadeAttractEngine(new Random(3));var settings=new ArcadeAttractSettings{Enabled=true,IdleSeconds=180,CycleSeconds=12};attract.SetGames(new[]{"a","b","c"});attract.Reset(0);attract.Tick(179999,true,settings);Check(!attract.Input.AttractActive,"not before idle deadline");attract.Tick(180000,true,settings);Check(attract.Input.AttractActive&&attract.CurrentGame!=null,"enter after three minutes");string exhibit=attract.CurrentGame;attract.Tick(192000,true,settings);Check(attract.CurrentGame!=exhibit,"cycle authentic game IDs independently of browser");Check(attract.Key(13,true,192001)&&!attract.Input.AttractActive,"wake consumes Select");Check(attract.Key(13,true,193000),"held wake repeats consumed");Check(attract.Key(13,false,193001)&&attract.Key(13,false,193002),"release and duplicate routed release consumed");Check(!attract.Key(13,true,193500),"fresh press works after release quarantine");
        foreach(int key in new[]{17,162,18,164,38,195,196,207,218}){attract.Input.Enter(200000);Check(attract.Key(key,true,200001),"wake any keyboard/controller code");Check(attract.Key(key,false,200002),"wake keyup consumed");}
        attract.Input.Enter(300000);Check(attract.Back(300001)&&attract.Back(300002),"System Back wake and duplicate consumed");attract.Tick(600000,false,settings);Check(!attract.Input.AttractActive,"never attract while away/launching");settings.Enabled=false;attract.Tick(900000,true,settings);Check(!attract.Input.AttractActive,"attract independent opt-out");
        Console.WriteLine("Phase E idle/wake/museum engine passed");Console.WriteLine("PASS: "+checks+" Arcade Experience assertions; Xbox audio/rendering still require hardware.");
    }
}
