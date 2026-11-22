using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace RetroPass
{
    public sealed partial class ArcadeAttractPresenter : UserControl
    {
        private bool motion;
        public ArcadeAttractPresenter()
        {
            InitializeComponent();SizeChanged+=(sender,args)=>BuildGrid();
        }
        internal void Begin(bool branded)
        {
            Visibility=Visibility.Visible;
            motion=new UISettings().AnimationsEnabled&&!new AccessibilitySettings().HighContrast;
            var root=ElementCompositionPreview.GetElementVisual(MuseumRoot);
            root.StopAnimation("Opacity");root.Opacity=1;
            if(motion)
            {
                var entrance=root.Compositor.CreateScalarKeyFrameAnimation();
                entrance.InsertKeyFrame(0,0);entrance.InsertKeyFrame(1,1);entrance.Duration=TimeSpan.FromMilliseconds(250);
                root.StartAnimation("Opacity",entrance);
            }
            Entrance.Visibility=branded?Visibility.Visible:Visibility.Collapsed;
            Entrance.Source=branded?new BitmapImage(new Uri("ms-appx:///Assets/Branding/TimsArcadeSplash.png")){DecodePixelWidth=1280}:null;
            BuildGrid();
            var visual=ElementCompositionPreview.GetElementVisual(NeonGrid);
            ElementCompositionPreview.SetIsTranslationEnabled(NeonGrid,true);
            if(motion)
            {
                var drift=visual.Compositor.CreateVector3KeyFrameAnimation();
                drift.InsertKeyFrame(0,Vector3.Zero);drift.InsertKeyFrame(1,new Vector3(0,18,0));
                drift.Duration=TimeSpan.FromSeconds(9);drift.Direction=AnimationDirection.Alternate;drift.IterationBehavior=AnimationIterationBehavior.Forever;
                visual.StartAnimation("Translation",drift);
            }
        }
        private void BuildGrid()
        {
            NeonGrid.Children.Clear();double w=ActualWidth,h=ActualHeight;if(w<=0||h<=0)return;
            var brush=new SolidColorBrush(Color.FromArgb(70,50,216,232));
            for(int i=-5;i<=5;i++)NeonGrid.Children.Add(new Line{X1=w/2,Y1=h*.38,X2=w/2+i*w/6,Y2=h,Stroke=brush,StrokeThickness=1});
            for(int i=1;i<=9;i++){double y=h*.38+h*.62*Math.Pow(i/9.0,2);NeonGrid.Children.Add(new Line{X1=0,Y1=y,X2=w,Y2=y,Stroke=brush,StrokeThickness=1});}
        }
        internal void Display(BitmapImage image,string title,string details,bool brand,int exhibit)
        {
            Logo.Source=image;TitleFallback.Text=brand?"TIM'S ARCADE":title;
            TitleFallback.Visibility=image==null||brand?Visibility.Visible:Visibility.Collapsed;
            Logo.Visibility=brand?Visibility.Collapsed:Visibility.Visible;
            TitleLabel.Text=brand?"THE NEON MARQUEE MUSEUM":title;DetailLabel.Text=brand?"":details;
            // Position and metadata move between exhibits, rather than forming a permanent HUD.
            Exhibit.Margin=new Thickness(64+(exhibit%3-1)*12,48+(exhibit%2)*8,64,48);
            var visual=ElementCompositionPreview.GetElementVisual(Exhibit);
            ElementCompositionPreview.SetIsTranslationEnabled(Exhibit,true);
            visual.StopAnimation("Opacity");visual.StopAnimation("Translation");
            if(motion)
            {
                var fade=visual.Compositor.CreateScalarKeyFrameAnimation();fade.InsertKeyFrame(0,0);fade.InsertKeyFrame(1,1);fade.Duration=TimeSpan.FromMilliseconds(650);
                visual.StartAnimation("Opacity",fade);
                var drift=visual.Compositor.CreateVector3KeyFrameAnimation();drift.InsertKeyFrame(0,new Vector3(-8,0,0));drift.InsertKeyFrame(1,new Vector3(8,0,0));drift.Duration=TimeSpan.FromSeconds(12);visual.StartAnimation("Translation",drift);
            }
            else{visual.Opacity=1;visual.Properties.InsertVector3("Translation",Vector3.Zero);}
        }
        internal void End()
        {
            Visibility=Visibility.Collapsed;Logo.Source=null;Entrance.Source=null;
            TitleFallback.Text=TitleLabel.Text=DetailLabel.Text="";
            ElementCompositionPreview.GetElementVisual(MuseumRoot).StopAnimation("Opacity");
            ElementCompositionPreview.GetElementVisual(NeonGrid).StopAnimation("Translation");
            var visual=ElementCompositionPreview.GetElementVisual(Exhibit);visual.StopAnimation("Opacity");visual.StopAnimation("Translation");
        }
    }
}
