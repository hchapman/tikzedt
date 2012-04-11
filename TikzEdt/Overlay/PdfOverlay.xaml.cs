﻿/*This file is part of TikzEdt.
 
TikzEdt is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
 
TikzEdt is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.
 
You should have received a copy of the GNU General Public License
along with TikzEdt.  If not, see <http://www.gnu.org/licenses/>.*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using TikzEdt.Parser;

using System.Text.RegularExpressions;
using System.Diagnostics;
using TikzEdt.Overlay;

namespace TikzEdt
{
    /// <summary>
    /// The PdfOverlay component is responsible for displaying the Wysiwyg layer on top of the displayed pdf.
    /// It contains all the logic for the Wysiwyg manipulations.
    /// It does not manipulate the sourcecode directly, but manipulates the TikzParseTree.
    /// All changes can be caught by subscribing to the OnModified event.
    /// (MainWindow subsribes to this event and updates the source code appropriately.)
    /// 
    /// Note on coordinates:
    /// Two sorts of coordinates are used:
    ///     (i)  Pixel coordinates (of course, since wpf needs them).
    ///     (ii) Absolute Cartesian Tikz coordinates. (Disregarding polar setting / coordinate transform).
    /// </summary>
    public partial class PdfOverlay : UserControl, TikzEdt.Overlay.IPdfOverlayView, Overlay.IOverlayShapeFactory
    {

        public Overlay.PdfOverlayModel TheModel { get; private set; }

        #region EVENTS
 
        /// <summary>
        /// This event is called when user selectes Jump To Source on an Overlay item.
        /// The parameter sender will contain the TikzParseItem the user wants to jump to.
        /// (Call its StartPosition() method to determine the text offset.)
        /// The MainWindow should subscribe to this event and mark the appropriate segment in the text editor.
        /// </summary>
        public event EventHandler<JumpToSourceEventArgs> JumpToSource;
        public class JumpToSourceEventArgs : EventArgs
        {
            public int JumpToPos;
            public int SelectionLength;
        }

        /// <summary>
        /// This event is called when the user requests some edits to the text that cannot be done by editing the parsetree.
        /// (Note: currently the parsetree does not support deleting nodes.)
        /// The Codeblock-commands use this event
        /// </summary>
        public event EventHandler<ReplaceTextEventArgs> ReplaceText;
        public class ReplaceTextEventArgs : EventArgs
        {
            public struct ReplaceData
            {
                public int StartPosition;
                public int Length;
                public string ReplacementText;
            }
            public IEnumerable<ReplaceData> Replacements;

            public ReplaceTextEventArgs() { }
            public ReplaceTextEventArgs( int tStartPosition, int tLength, string tReplacementText) 
            {
                var l = new List<ReplaceData>();
                l.Add(new ReplaceData() { StartPosition = tStartPosition, Length=tLength, ReplacementText=tReplacementText });
                Replacements = l;
            } 
        }

        #endregion

        #region PROPERTIES
        public static readonly DependencyProperty NodeStyleProperty = DependencyProperty.Register(
        "NodeStyle", typeof(string), typeof(PdfOverlay), new PropertyMetadata(""));
        /// <summary>
        /// The style applied to nodes created using the overlay tools.
        /// </summary>
        public string NodeStyle
        {
            get { return (string)GetValue(NodeStyleProperty); }
            set { SetValue(NodeStyleProperty, value); }
        }
        public static readonly DependencyProperty EdgeStyleProperty = DependencyProperty.Register(
        "EdgeStyle", typeof(string), typeof(PdfOverlay), new PropertyMetadata(""));
        /// <summary>
        /// The style applied to edges created using the overlay tools.
        /// </summary>
        public string EdgeStyle
        {
            get { return (string)GetValue(EdgeStyleProperty); }
            set { SetValue(EdgeStyleProperty, value); }
        }

        public static readonly DependencyProperty NewNodeModifierProperty = DependencyProperty.Register(
            "NewNodeModifier", typeof(string), typeof(PdfOverlay), new PropertyMetadata(""));
        /// <summary>
        /// Determines the decoration for new coordinates, i.e., "" or "+" or "++".
        /// </summary>
        public string NewNodeModifier
        {
            get { return (string)GetValue(NewNodeModifierProperty); }
            set { SetValue(NewNodeModifierProperty, value); }
        }
        public static readonly DependencyProperty UsePolarCoordinatesProperty = DependencyProperty.Register(
            "UsePolarCoordinates", typeof(bool), typeof(PdfOverlay), new PropertyMetadata(false, OnUsePolarCoordinatesChange));
        static void OnUsePolarCoordinatesChange(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            (sender as PdfOverlay).TheModel.CurrentTool.UpdateRaster();
        }
        /// <summary>
        /// Determines whether newly created coordinates should be polar or Cartesian.
        /// </summary>
        public bool UsePolarCoordinates
        {
            get { return (bool)GetValue(UsePolarCoordinatesProperty); }
            set { SetValue(UsePolarCoordinatesProperty, value); }
        }


        /// <summary>
        /// This property determines when the overlay can be edited by the user.
        /// For TikzEdt, it is should be set to false whenever the document is out of sync with the current parsetree.
        /// This happens (i) while parsing a recent change and (ii) upon parse error
        /// </summary>
        public bool AllowEditing
        {
            get { return (bool)GetValue(AllowEditingProperty); }
            set { SetValue(AllowEditingProperty, value); }
        }        
        public static readonly DependencyProperty AllowEditingProperty =
            DependencyProperty.Register("AllowEditing", typeof(bool), typeof(PdfOverlay), new UIPropertyMetadata(true));
        
        readonly public static DependencyProperty ParseTreeProperty = DependencyProperty.Register(
                "ParseTree", typeof(Tikz_ParseTree), typeof(PdfOverlay), new PropertyMetadata(null, OnParseTreeChanged));
        static void OnParseTreeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            PdfOverlay po = d as PdfOverlay;
 //           if (e.OldValue != null)
 //               (e.OldValue as Tikz_ParseTree).TextChanged -= po._parsetree_TextChanged;
 //           if (po.ParseTree != null)
 //               po.ParseTree.TextChanged += new Tikz_ParseTree.TextChangedHandler(po._parsetree_TextChanged);
            // reset current tool
            po.TheModel.CurrentTool.OnDeactivate();
            po.TheModel.CurrentTool.OnActivate();
            po.TheModel.RedrawObjects();
        }
        /// <summary>
        /// The Parse tree currently being displayed is stored in this property.
        /// </summary>
        public Tikz_ParseTree ParseTree
        {
            get { return (Tikz_ParseTree)GetValue(ParseTreeProperty); }
            set { SetValue(ParseTreeProperty, value); }
        }

        void AdjustSize()
        {
            Width = BB.Width * Resolution;
            Height = BB.Height * Resolution;
            TheModel.AdjustPositions();
        }

        readonly public static DependencyProperty ResolutionProperty = DependencyProperty.Register(
                "Resolution", typeof(double), typeof(PdfOverlay), new PropertyMetadata(Consts.ptspertikzunit,
                    new PropertyChangedCallback(OnBBChanged)));
        /// <summary>
        /// The current resolution, in dots per Tikz unit (cm).
        /// </summary>
        public double Resolution
        {
            get { return (double)GetValue(ResolutionProperty); }
            set { SetValue(ResolutionProperty, value); }
        }

        readonly public static DependencyProperty BBProperty = DependencyProperty.Register(
            "BB", typeof(Rect), typeof(PdfOverlay), new PropertyMetadata(new Rect(0,0,10,10), 
                new PropertyChangedCallback( OnBBChanged )));
        static void OnBBChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            PdfOverlay po = d as PdfOverlay;
            po.AdjustSize();
        }
        /// <summary>
        /// The current bounding box.
        /// </summary>
        public Rect BB
        {
            get { return (Rect)GetValue(BBProperty); }
            set { SetValue(BBProperty, value); }
        }



        public Canvas canvas { get { return canvas1; } }
        
        public RasterControlModel Rasterizer { get; set; }


        public static readonly DependencyProperty toolProperty = DependencyProperty.Register("Tool", typeof(OverlayToolType), typeof(PdfOverlay),
                            new PropertyMetadata(OverlayToolType.move, new PropertyChangedCallback(OnToolChanged)));
        static void OnToolChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            PdfOverlay po = sender as PdfOverlay;
            po.TheModel.ToolList[(int)e.OldValue].OnDeactivate();
            po.TheModel.CurrentTool.OnActivate();
        }
        public OverlayToolType Tool
        {
            get { return (OverlayToolType)GetValue(toolProperty); }
            set { SetValue(toolProperty, value); }
        }

        #endregion

        #region MarkObjectAt

        public void MarkObject(IOverlayShapeView vv)
        {
            Shape v = vv as Shape;
            if (v == null)
                return;

            MarkerCenter = new Point(Canvas.GetLeft(v) + v.Width / 2, Canvas.GetBottom(v) + v.Height / 2);
            MarkerEllipse.Width = Math.Max(v.Width, v.Height);
            //MarkerEllipse.Width = Math.Max(ols.ActualWidth, ols.ActualHeight);
            //da.KeyFrames.Add(new DoubleKeyFrame());
            Canvas.SetBottom(MarkerEllipse, MarkerCenter.Y - MarkerEllipse.ActualHeight / 2);
            Canvas.SetLeft(MarkerEllipse, MarkerCenter.X - MarkerEllipse.ActualWidth / 2);

            if (!canvas1.Children.Contains(MarkerEllipse))
                canvas1.Children.Add(MarkerEllipse);
            Storyboard anim = (Storyboard)FindResource("MarkerAnimation");
            anim.Begin(this);
            MarkerEllipse.Visibility = System.Windows.Visibility.Visible;
        }

        Point MarkerCenter = new Point(100, 100);
        private void DoubleAnimationUsingKeyFrames_Changed(object sender, EventArgs e)
        {
            if (MarkerEllipse != null)
            {
                Canvas.SetBottom(MarkerEllipse, MarkerCenter.Y - MarkerEllipse.ActualHeight / 2);
                Canvas.SetLeft(MarkerEllipse, MarkerCenter.X - MarkerEllipse.ActualWidth / 2);
            }
        }

        private void Storyboard_Completed(object sender, EventArgs e)
        {
            MarkerEllipse.Visibility = Visibility.Hidden;
        }
        #endregion

        /// <summary>
        ///  Resets the current tool to the default (= the move tool).
        /// </summary>
        public void ActivateDefaultTool()
        {
            Tool = OverlayToolType.move;
        }
        
        public PdfOverlay()
        {          
            InitializeComponent();

            TheModel = new Overlay.PdfOverlayModel(this, this); // call this after InitializeComponent() so that UI is available

            // allow to gain keyboard focus
            canvas.Focusable = true;
            
            // handle delete event
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, DeleteCommandHandler));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, CopyCommandHandler));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, CutCommandHandler));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, PasteCommandHandler));
            
        }

        void DeleteCommandHandler(object sender, ExecutedRoutedEventArgs e)
        {
            mnuSelection_Click(mnuSelectionDelete, null);
        }
        void CopyCommandHandler(object sender, ExecutedRoutedEventArgs e)
        {
            mnuSelection_Click(mnuSelectionCopy, null);
        }
        void CutCommandHandler(object sender, ExecutedRoutedEventArgs e)
        {
            mnuSelection_Click(mnuSelectionCut, null);
        }
        void PasteCommandHandler(object sender, ExecutedRoutedEventArgs e)
        {
            MessageBox.Show("Pasting in the WYSIWYG part is not supported. Please paste directly into the text editor on the left.", "Paste", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>
        /// Sets the current parsetree and updates the overlay.
        /// </summary>
        /// <param name="t">The new parsetree.</param>
        /// <param name="tBB">The new bounding box.</param>
  /*      public void SetParseTree(Tikz_ParseTree t, Rect tBB)
        {
            BB = tBB;
            ParseTree = t;
            //curSel = null; //curDragged = null;
            Resolution = Resolution; // to recalc size
            ActivateDefaultTool(); // to reset current tool
            RedrawObjects();
        } */


        private void canvas1_MouseMove(object sender, MouseEventArgs e)
        {
            Point mousep = e.GetPosition(canvas1);
            // convert to bottom left coordinates
            Point p = new Point(mousep.X, Height - mousep.Y);

            TheModel.CurrentTool.OnMouseMove(p, e);
            
            // display the current mouse position
         /*   p.Y /= Resolution;
            p.X /= Resolution;
            p.X += BB.X;
            p.Y += BB.Y;

            String s = "(" + String.Format("{0:f1}", p.X) + "; " + String.Format("{0:f1}", p.Y) + ")";
            ((MainWindow)Application.Current.Windows[0]).AddStatusBarCoordinate(s);            
         */ 
        }

        private void canvas1_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {            
            // for some unknown reason the focus has to be set using the dispatcher...
            Dispatcher.BeginInvoke(new Action(delegate() { Keyboard.Focus(canvas); }));            
            
            // call left down-method in the current tool
            Point mousep = e.GetPosition(canvas1);
            object oo = canvas1.InputHitTest(mousep);
            if (! ( oo is OverlayShape))
                oo = null;
            TheModel.CurrentTool.OnLeftMouseButtonDown(oo as OverlayShape, new Point(mousep.X, Height - mousep.Y), e);
        }


        private void canvas1_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {

            if (canvas1.IsMouseCaptured)
                canvas1.ReleaseMouseCapture();  // release mouse capture here to make sure the tools cannot forget
            Point mousep = e.GetPosition(canvas1);
            TheModel.CurrentTool.OnLeftMouseButtonUp(e, new Point(mousep.X, Height - mousep.Y));

        }


        /// <summary>
        /// Raises the Jumptosource event.
        /// Note that the object to be jumped to might not be at the current mouse position.
        /// (Namely, if the context menu was opened->mouse position changed to menu item-> clicked)
        /// It is stored in mnuJumpSource.Tag
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void JumpToSourceDoIt(object sender, RoutedEventArgs e)
        {
            if (sender != mnuJumpSource)
            {
                // jump to object at mouse position
                IInputElement o = canvas1.InputHitTest(Mouse.GetPosition(canvas1));
                if (o is OverlayShape)
                {
                    mnuJumpSource.Tag = o;
                }
                else mnuJumpSource.Tag = null;
            }

            if (mnuJumpSource.Tag != null)
            {
                JumpToSourceDoIt(mnuJumpSource.Tag as OverlayShape);
            }
        }
        public void JumpToSourceDoIt(OverlayShape o)
        {
            if (JumpToSource != null)
            {
                TikzParseItem tpi = o.item; 
                if (tpi != null)
                    JumpToSource(this, new JumpToSourceEventArgs() { JumpToPos = tpi.StartPosition(), SelectionLength = tpi.Length });
            }
        }
        private void contextmenuClick(object sender, RoutedEventArgs e)
        {
            if (sender == mnuMove)
                Tool = OverlayToolType.move;
            else if (sender == mnuAddNode)
                Tool = OverlayToolType.addvert;
            else if (sender == mnuAddEdge)
                Tool = OverlayToolType.addedge;
            else if (sender == mnuAddPath)
                Tool = OverlayToolType.addpath;
            else if (sender == mnuRectangle)
                Tool = OverlayToolType.rectangle;
            else if (sender == mnuEllipse)
                Tool = OverlayToolType.ellipse;
            else if (sender == mnuGrid)
                Tool = OverlayToolType.grid;
            else if (sender == mnuSmooth)
                Tool = OverlayToolType.smooth;
            else if (sender == mnuBezier)
                Tool = OverlayToolType.bezier;
            else if (sender == mnuArc)
                Tool = OverlayToolType.arc;
            else if (sender == mnuArcEdit)
                Tool = OverlayToolType.arcedit;
            else if (sender == mnuJumpSource)
            {
                JumpToSourceDoIt(sender, e);
            }
            else if (sender == mnuEdit)
            {
                TheModel.CurEditing = mnuJumpSource.Tag as OverlayScope;
            }
        }

        bool PreventContextMenuOpening = false;
        private void canvas1_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (PreventContextMenuOpening)
            {
                e.Handled = true;
                PreventContextMenuOpening = false;
                return;
            }

            mnuMove.IsChecked = (Tool == OverlayToolType.move);
            mnuAddNode.IsChecked = (Tool == OverlayToolType.addvert);
            mnuAddEdge.IsChecked = (Tool == OverlayToolType.addedge);
            mnuAddPath.IsChecked = (Tool == OverlayToolType.addpath);

            
            IInputElement o = canvas1.InputHitTest(Mouse.GetPosition(canvas1));

            // some commands in the context menu (Jump to source, editing) operate on the object at mouse position
            // when the contextmenu opens. Since this position is lost when clicking the menu item, the object there 
            // has to be stored somewhere -> in the mnuJumpSource.Tag
            if (o is OverlayShape)
            {
                mnuJumpSource.Tag = o;
            }
            else mnuJumpSource.Tag = null;
            mnuJumpSource.IsEnabled = (mnuJumpSource.Tag != null);
            mnuEdit.IsEnabled = (o is OverlayScope);
        }

        /// <summary>
        /// The standard handling of right click is as follows (with this priority): 
        ///   1. The current tool uses the click.
        ///   2. Set the tool to the standard tool (move)
        ///   3. Deselect the CurEditing item.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void canvas1_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // call right down-method in the current tool
            Point mousep = e.GetPosition(canvas1);
            object oo = canvas1.InputHitTest(mousep);
            if (!(oo is OverlayShape))
                oo = null;
            TheModel.CurrentTool.OnRightMouseButtonDown(oo as OverlayShape, new Point(mousep.X, Height - mousep.Y), e);
            
            // if the tool didn't use the click-> proceed with standard handling
            if (!e.Handled)
            {
                if (Tool == OverlayToolType.move)
                {
                    //canvas1.ContextMenu.IsEnabled = true;
                    if (TheModel.CurEditing != null)
                    {
                        TheModel.CurEditing = null;
                        PreventContextMenuOpening = true;
                    }
                }
                else
                {
                    Tool = OverlayToolType.move;
                    PreventContextMenuOpening = true;
                }
            }
            else 
                PreventContextMenuOpening = true;
        }

        private void canvas1_KeyDown(object sender, KeyEventArgs e)
        {
            // route event to current tool
            TheModel.CurrentTool.KeyDown(e);

            // turn off raster on Alt
            Rasterizer.View.OverrideWithZeroGridWidth = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            Rasterizer.View.OverrideWithHalfGridWidth = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
                e.Handled = true;

            if (!e.Handled)
            {
                // escape cancels current operation
                if (e.Key == Key.Escape)
                    ActivateDefaultTool();
               
            }

        }

        private void canvas1_KeyUp(object sender, KeyEventArgs e)
        {
            // route event to current tool
            TheModel.CurrentTool.KeyUp(e);

            // turn on raster on Alt released
            Rasterizer.View.OverrideWithZeroGridWidth = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            Rasterizer.View.OverrideWithHalfGridWidth = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
                e.Handled = true;
        }

        private void mnuAssignStyle_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mnuAssignCurrentNodeStyle)
                TheModel.AssignStyle(PdfOverlayModel.AssignStyleType.AssignCurrentNodeStyle );
            else if (sender == mnuAssignNewStyle)
                TheModel.AssignStyle(PdfOverlayModel.AssignStyleType.AssignNewStyle );
            else if (sender == mnuChangeToCurrentNodeStyle)
                TheModel.AssignStyle(PdfOverlayModel.AssignStyleType.ChangeToCurrentNodeStyle );
            else if (sender == mnuChangeToNewStyle)
                TheModel.AssignStyle(PdfOverlayModel.AssignStyleType.ChangeToNewStyle );
        }

        private void canvas1_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // in case the keyboard focus is lost while alt or shift+alt pressed, the raster has to be made reappear
            Rasterizer.View.OverrideWithHalfGridWidth = false;
            Rasterizer.View.OverrideWithZeroGridWidth = false; 
        }

        private void mnuSelection_Click(object sender, RoutedEventArgs e)
        {
            if (Tool != OverlayToolType.move)
                return;

            List<TikzParseItem> FullSelection = TikzParseTreeHelper.GetFullSelection(TheModel.selectionTool.SelItems.Select(ols => ols.item));

            if (!FullSelection.Any())
                return;

            // get codeblock text
            string cbtext = "", cbtextE="";
            foreach (var tpi in FullSelection)
                cbtext += tpi.ToString() + Environment.NewLine;

            // if the selected items are within a path, enscope by adding { }. If they are within another scope or the tikzpicture, enscope by \begin{scope} \end{scope}
            TikzContainerParseItem tc = FullSelection.First().parent;
            if (tc is Tikz_Picture || tc is Tikz_Scope)
                cbtextE = "\\begin{scope}[]" + Environment.NewLine + cbtext + Environment.NewLine + "\\end{scope}" + Environment.NewLine;
            else
                cbtextE = " { " + cbtext + " } ";


            var ReplacementList = new List<ReplaceTextEventArgs.ReplaceData>();

            if (sender == mnuSelectionCopy)
            {
                Clipboard.SetText(cbtext);
            }
            else if (sender == mnuSelectionCopyE)
            {
                Clipboard.SetText(cbtextE);
            }
            else if (sender == mnuSelectionDelete || sender == mnuSelectionCut || sender == mnuSelectionCutE)
            {
                if (ReplaceText != null)
                {
                    foreach (var tpi in FullSelection)
                        ReplacementList.Insert(0, new ReplaceTextEventArgs.ReplaceData() { StartPosition = tpi.StartPosition(), Length = tpi.Length, ReplacementText = "" });

                    if (sender == mnuSelectionCut)
                        Clipboard.SetText(cbtext);
                    else if (sender == mnuSelectionCutE)
                        Clipboard.SetText(cbtextE);

                    ReplaceText(this, new ReplaceTextEventArgs() { Replacements = ReplacementList });
                }
            }
            else if (sender == mnuSelectionCollect || sender == mnuSelectionCollectE)
            {
                if (ReplaceText != null)
                {
                    // Text to delete ... mind the order
                    foreach (var tpi in FullSelection)
                        ReplacementList.Insert(0, new ReplaceTextEventArgs.ReplaceData() { StartPosition = tpi.StartPosition(), Length = tpi.Length, ReplacementText = "" });

                    // text to insert (text of selected nodes, gathered together, optionally enscoped
                    if (sender == mnuSelectionCollect)
                    {
                        ReplacementList.Add(new ReplaceTextEventArgs.ReplaceData() 
                        {
                            StartPosition = FullSelection.First().StartPosition(),
                            Length = 0,
                            ReplacementText = cbtext
                        });
                    }
                    else
                    {
                        ReplacementList.Add(new ReplaceTextEventArgs.ReplaceData()
                        {
                            StartPosition = FullSelection.First().StartPosition(),
                            Length = 0,
                            ReplacementText = cbtextE
                        });
                    }

                    ReplaceText(this, new ReplaceTextEventArgs() { Replacements = ReplacementList });
                }
            }
        }

        /*private void mnuCodeBlockMark_Click(object sender, RoutedEventArgs e)
        {
            if (Tool != OverlayToolType.move)
                return;

            List<TikzParseItem> CodeBlock = TikzParseTreeHelper.GetCodeBlock( selectionTool.SelItems.Select( ols => ols.item ) );

            if (!CodeBlock.Any()) 
                return;
            
            int startpos = CodeBlock.First().StartPosition();
            int endpos = CodeBlock.Last().StartPosition() + CodeBlock.Last().Length;

            // get codeblock text
            string cbtext = "";
            foreach (var tpi in CodeBlock)
                cbtext += tpi.ToString();

            if (sender == mnuCodeBlockMark)
            {
                if (JumpToSource != null)
                {
                    int i1 = CodeBlock.First().StartPosition();
                    int i2 = CodeBlock.Last().StartPosition() + CodeBlock.Last().Length;
                    JumpToSource(this, new JumpToSourceEventArgs() { JumpToPos=i1, SelectionLength=i2-i1 });
                }
            }
            else if (sender == mnuCodeBlockCopy)
            {
                Clipboard.SetText(cbtext);
            }
            else if (sender == mnuCodeBlockDelete)
            {
                if (ReplaceText != null)
                    ReplaceText(this, new ReplaceTextEventArgs() { StartPosition = startpos, Length = endpos - startpos, ReplacementText = "" });
            }
            else if (sender == mnuCodeBlockEnscope || sender == mnuCodeBlockEDuplicate)
            {
                if (ReplaceText != null)
                {
                    // if the selected items are within a path, enscope by adding { }. If they are within another scope or the tikzpicture, enscope by \begin{scope} \end{scope}
                    TikzContainerParseItem tc = CodeBlock.First().parent;
                    string NewText;
                    if (tc is Tikz_Picture || tc is Tikz_Scope)
                        NewText = "\\begin{scope}[]" + Environment.NewLine + cbtext + Environment.NewLine + "\\end{scope}" + Environment.NewLine;
                    else
                        NewText = " { " + cbtext + " } ";

                    if (sender == mnuCodeBlockEnscope)
                    {
                        ReplaceText(this, new ReplaceTextEventArgs()
                        {
                            StartPosition = startpos,
                            Length = endpos - startpos,
                            ReplacementText = NewText
                        });
                    }
                    else
                    {
                        ReplaceText(this, new ReplaceTextEventArgs()
                        {
                            StartPosition = endpos,
                            Length = 0,
                            ReplacementText = NewText
                        });
                    }
                }
            }
        } */

        private void mnuCodeBlockWhatsThis_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("The Wysiwyg interface is \"aware of\" coordinates in the code, but not about other Tikz objects (rectangles etc.). "+
                "If you mark some coordinates and press delete (for example), Tikzedt has no way to know what part of the Tikz code exactly you want to delete. "+
                "It uses a heuristic to determine what to do."+
                "This might or might not be waht you expect. If not, you have to undo and edit the code manually."+
                "Note also that the Delete/Cut/Collect operations may produce a non-compiling Tikzfile, depending on what you delete/cut/collect.",
                "This is not doing what you want?", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void Clear()
        {
            canvas1.Children.Clear();
        }

        Overlay.IOverlayShapeView Overlay.IOverlayShapeFactory.NewNodeView()
        {
            OverlayNodeView v = new OverlayNodeView();
            canvas1.Children.Add(v);
            return v;
        }

        Overlay.IOverlayScopeView Overlay.IOverlayShapeFactory.NewScopeView()
        {
            OverlayScopeView v = new OverlayScopeView();
            canvas1.Children.Add(v);
            return v;
        }

        Overlay.IOverlayCPView Overlay.IOverlayShapeFactory.NewCPView()
        {
            OverlayCPView v = new OverlayCPView();
            canvas1.Children.Add(v);
            canvas1.Children.Add(v.lineToOrigin1);
            canvas1.Children.Add(v.lineToOrigin2);
            return v;
        }

        IRectangleShape IOverlayShapeFactory.GetSelectionRect()
        {
            WPFRectangle SelectionRect = new WPFRectangle(canvas1);

            SelectionRect.TheRectangle.Stroke = Brushes.Blue;
            SelectionRect.TheRectangle.StrokeThickness = 1;
            SelectionRect.TheRectangle.Visibility = Visibility.Collapsed;
            SelectionRect.TheRectangle.Fill = new SolidColorBrush(Color.FromArgb(0x23, 0x00, 0x8A, 0xCA));
            SelectionRect.TheRectangle.Fill.Freeze();
            return SelectionRect;
        }



        public void SetCursor(System.Windows.Forms.Cursor cursor)
        {
            Cursor c;
                if (cursor == System.Windows.Forms.Cursors.Arrow) c = Cursors.Arrow; 
                else if (cursor ==  System.Windows.Forms.Cursors.Hand) c= Cursors.Hand;
                else if (cursor ==  System.Windows.Forms.Cursors.UpArrow) c= Cursors.UpArrow; 
                else if (cursor ==  System.Windows.Forms.Cursors.Cross) c= Cursors.Cross;
                else throw new NotImplementedException();
            
            canvas1.Cursor = c;
        }


        IRectangleShape IOverlayShapeFactory.GetCPCircle()
        {
            WPFEllipse e = new WPFEllipse(canvas1);
            e.TheEllipse.Width = e.TheEllipse.Height = 10;
            e.TheEllipse.Stroke = Brushes.Red;
            e.TheEllipse.Fill = Brushes.Gray;
            return e;
        }

        IRectangleShape IOverlayShapeFactory.GetPreviewEllipse()
        {
            throw new NotImplementedException();
        }

        IRectangleShape IOverlayShapeFactory.GetPreviewRectangle()
        {
            throw new NotImplementedException();
        }

        IFanShape IOverlayShapeFactory.GetPreviewFan()
        {

            PreviewArc.Stroke = Brushes.Black;
            PreviewArc.StrokeDashArray = new DoubleCollection(new double[] { 4, 4 });
            PreviewArc.Visibility = Visibility.Collapsed;
        }

        IRectangleShape IOverlayShapeFactory.GetPreviewGrid()
        {
            throw new NotImplementedException();
        }


        public bool MouseCaptured
        {
            set 
            {
                if (value && !canvas1.IsMouseCaptured)
                    canvas1.CaptureMouse();
                if (!value && canvas1.IsMouseCaptured)
                    canvas1.ReleaseMouseCapture();
            }
        }
    }



    

}
