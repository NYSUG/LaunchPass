using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LaunchPass.Catalog
{
    public sealed class ArcadeSessionPolicy
    {
        public bool Foreground, InternalFrontend, PremiumVisible, Ready, Dialog, Launching, Failed;
        public bool CanAttract => Foreground && InternalFrontend && PremiumVisible && Ready && !Dialog && !Launching && !Failed;
        public bool CanPlayMusic => Foreground && InternalFrontend && !Launching && !Failed;
        public bool ShowSplash(bool enabled) => enabled && PremiumVisible && !Ready && !Failed;
    }
    [DataContract] public sealed class RadioCrossfadeSettings
    {
        [DataMember(Name="enabled")] public bool Enabled { get; set; }
        [DataMember(Name="seconds")] public double Seconds { get; set; } = 3;
        [OnDeserializing] private void Defaults(StreamingContext c) { Seconds = 3; }
    }
    [DataContract] public sealed class ArcadeMusicSettings
    {
        [DataMember(Name="enabled")] public bool Enabled { get; set; }
        [DataMember(Name="volume")] public double Volume { get; set; } = .2;
        [DataMember(Name="muted")] public bool Muted { get; set; }
        [DataMember(Name="crossfade")] public RadioCrossfadeSettings Crossfade { get; set; } = new RadioCrossfadeSettings();
        [OnDeserializing] private void Defaults(StreamingContext c) { Volume = .2; Crossfade = new RadioCrossfadeSettings(); }
    }
    [DataContract] public sealed class ArcadeFeatureSettings
    {
        [DataMember(Name="enabled")] public bool Enabled { get; set; } = true;
        [OnDeserializing] private void Defaults(StreamingContext c) { Enabled = true; }
    }
    [DataContract] public sealed class ArcadeAttractSettings
    {
        [DataMember(Name="enabled")] public bool Enabled { get; set; }
        [DataMember(Name="idleSeconds")] public double IdleSeconds { get; set; } = 180;
        [DataMember(Name="cycleSeconds")] public double CycleSeconds { get; set; } = 12;
        [OnDeserializing] private void Defaults(StreamingContext c) { IdleSeconds = 180; CycleSeconds = 12; }
    }
    [DataContract] public sealed class ArcadeExperienceSettings
    {
        [DataMember(Name="version",IsRequired=true)] public int Version { get; set; } = 1;
        [DataMember(Name="music")] public ArcadeMusicSettings Music { get; set; } = new ArcadeMusicSettings();
        [DataMember(Name="branding")] public ArcadeFeatureSettings Branding { get; set; } = new ArcadeFeatureSettings();
        [DataMember(Name="splash")] public ArcadeFeatureSettings Splash { get; set; } = new ArcadeFeatureSettings();
        [DataMember(Name="attract")] public ArcadeAttractSettings Attract { get; set; } = new ArcadeAttractSettings();
        [OnDeserializing] private void Defaults(StreamingContext c)
        { Music = new ArcadeMusicSettings(); Branding = new ArcadeFeatureSettings(); Splash = new ArcadeFeatureSettings(); Attract = new ArcadeAttractSettings(); }
        public static ArcadeExperienceSettings Parse(string json)
        {
            string text = (json ?? "").Trim(); bool quoted = false, escaped = false; int depth = 0;
            if (text.Length == 0 || text[0] != '{') throw new FormatException("Expected an ArcadeExperience object.");
            for (int i=0;i<text.Length;i++)
            {
                char ch=text[i];
                if (quoted) { if (escaped) escaped=false; else if (ch=='\\') escaped=true; else if(ch=='"') quoted=false; }
                else if(ch=='"') quoted=true;
                else if(ch=='{' || ch=='[') depth++;
                else if(ch=='}' || ch==']') { if(--depth==0 && i!=text.Length-1) throw new FormatException("Trailing settings data."); }
            }
            ArcadeExperienceSettings value;
            try
            {
                using(var reader=JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(text),System.Xml.XmlDictionaryReaderQuotas.Max))
                    value=(ArcadeExperienceSettings)new DataContractJsonSerializer(typeof(ArcadeExperienceSettings)).ReadObject(reader);
            }
            catch(Exception ex) when(ex is SerializationException || ex is System.Xml.XmlException) { throw new FormatException("Malformed ArcadeExperience.json.",ex); }
            if(value==null || value.Version!=1) throw new FormatException("ArcadeExperience version must be 1.");
            value.Music=value.Music??new ArcadeMusicSettings(); value.Music.Crossfade=value.Music.Crossfade??new RadioCrossfadeSettings();
            value.Branding=value.Branding??new ArcadeFeatureSettings(); value.Splash=value.Splash??new ArcadeFeatureSettings(); value.Attract=value.Attract??new ArcadeAttractSettings();
            if(!Finite(value.Music.Volume) || value.Music.Volume<0 || value.Music.Volume>1) throw new FormatException("Music volume must be 0..1.");
            if(!Finite(value.Music.Crossfade.Seconds) || value.Music.Crossfade.Seconds<2 || value.Music.Crossfade.Seconds>4) throw new FormatException("Crossfade seconds must be 2..4.");
            if(!Finite(value.Attract.IdleSeconds) || value.Attract.IdleSeconds<15 || value.Attract.IdleSeconds>3600) throw new FormatException("Idle seconds must be 15..3600.");
            if(!Finite(value.Attract.CycleSeconds) || value.Attract.CycleSeconds<5 || value.Attract.CycleSeconds>60) throw new FormatException("Cycle seconds must be 5..60.");
            return value;
        }
        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
    }

    public sealed class ArcadeShuffleBag
    {
        private readonly Random random;
        private List<string> library=new List<string>(), bag=new List<string>();
        public string Previous { get; private set; }
        public int Count => library.Count;
        public ArcadeShuffleBag(Random random=null) { this.random=random??new Random(); }
        public void SetItems(IEnumerable<string> values)
        {
            var next=values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).ToList();
            if(library.SequenceEqual(next,StringComparer.OrdinalIgnoreCase)) return;
            library=next; bag.Clear();
        }
        public void Exclude(string id) { library.RemoveAll(s=>StringComparer.OrdinalIgnoreCase.Equals(s,id)); bag.RemoveAll(s=>StringComparer.OrdinalIgnoreCase.Equals(s,id)); }
        public string Next()
        {
            if(library.Count==0) return null;
            if(bag.Count==0)
            {
                bag=new List<string>(library);
                for(int i=bag.Count-1;i>0;i--) { int j=random.Next(i+1); string swap=bag[i];bag[i]=bag[j];bag[j]=swap; }
                if(bag.Count>1 && StringComparer.OrdinalIgnoreCase.Equals(bag[0],Previous)) { string swap=bag[0];bag[0]=bag[1];bag[1]=swap; }
            }
            string result=bag[0]; bag.RemoveAt(0); Previous=result; return result;
        }
    }

    public sealed class ArcadeActivityGate
    {
        private readonly HashSet<int> suppressed=new HashSet<int>();
        private double quietUntil;
        public bool AttractActive { get; private set; }
        public double LastActivity { get; private set; }
        public bool Quiet(double now) => now<quietUntil || suppressed.Count>0;
        private static int Normalize(int key) => key==160||key==161?16:key==162||key==163?17:key==164||key==165?18:key;
        public void Enter(double now) { AttractActive=true; suppressed.Clear(); quietUntil=0; }
        public bool Key(int key,bool down,double now)
        {
            key=Normalize(key);LastActivity=now;
            if(AttractActive) { AttractActive=false;quietUntil=now+250;suppressed.Add(key); }
            bool consume=now<quietUntil || suppressed.Contains(key);
            if(consume) { if(down) suppressed.Add(key);else { suppressed.Remove(key);quietUntil=now+250; } }
            return consume;
        }
        public bool SystemBack(double now)
        { LastActivity=now;if(AttractActive){AttractActive=false;quietUntil=now+250;return true;}return Quiet(now); }
        public void Wake(double now) { AttractActive=false;LastActivity=now;quietUntil=now+250; }
        public void Reset(double now) { AttractActive=false;suppressed.Clear();quietUntil=0;LastActivity=now; }
        public void Touch(double now) { LastActivity=now; }
    }
    public sealed class ArcadeAttractEngine
    {
        private readonly ArcadeShuffleBag games;
        private double changed;
        public ArcadeActivityGate Input { get; } = new ArcadeActivityGate();
        public string CurrentGame { get; private set; }
        public int Exhibits { get; private set; }
        public event Action<string> Event;
        public ArcadeAttractEngine(Random random=null) { games=new ArcadeShuffleBag(random); }
        public void SetGames(IEnumerable<string> ids) { games.SetItems(ids); }
        public void Tick(double now,bool eligible,ArcadeAttractSettings settings)
        {
            if(!eligible || !settings.Enabled || games.Count==0) { bool was=Input.AttractActive;Input.Reset(now);if(was)Event?.Invoke("attract-exit");return; }
            if(!Input.AttractActive && !Input.Quiet(now) && now-Input.LastActivity>=settings.IdleSeconds*1000)
            { Input.Enter(now);Exhibits=0;Event?.Invoke("attract-enter");Next(now); }
            else if(Input.AttractActive && now-changed>=settings.CycleSeconds*1000) Next(now);
        }
        private void Next(double now) { CurrentGame=games.Next();changed=now;Exhibits++;Event?.Invoke("attract-game"); }
        public bool Key(int key,bool down,double now)
        { bool was=Input.AttractActive;bool result=Input.Key(key,down,now);if(was&&!Input.AttractActive)Event?.Invoke("attract-exit");return result; }
        public bool Back(double now) { bool was=Input.AttractActive;bool result=Input.SystemBack(now);if(was)Event?.Invoke("attract-exit");return result; }
        public void Activity(double now) { bool was=Input.AttractActive;Input.Wake(now);if(was)Event?.Invoke("attract-exit"); }
        public void Reset(double now) { bool was=Input.AttractActive;Input.Reset(now);if(was)Event?.Invoke("attract-exit"); }
    }
}
