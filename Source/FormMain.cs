/*
 * Licensed to De Bitmanager under one or more contributor
 * license agreements. See the NOTICE file distributed with
 * this work for additional information regarding copyright
 * ownership. De Bitmanager licenses this file to you under
 * the Apache License, Version 2.0 (the "License"); you may
 * not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied.  See the License for the
 * specific language governing permissions and limitations
 * under the License.
 */

using BrightIdeasSoftware;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Threading;
using System.Text;
using Bitmanager.Core;
using Bitmanager.BigFile.Query;
using System.Runtime;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Bitmanager.IO;
using System.Diagnostics;
using Bitmanager.Query;
using System.Reflection;

namespace Bitmanager.BigFile
{
   /// <summary>
   /// Main form for the application
   /// </summary>
   public partial class FormMain : Form, ILogFileCallback
   {
      private static Logger logger = Globals.MainLogger;
      private LogFile lf;
      private readonly SynchronizationContext synchronizationContext;
      private readonly SearchHistory searchboxDriver;
      private CancellationTokenSource cancellationTokenSource;
      private bool processing;
      private int prevGoto;
      private Settings settings;
      private FixedFontMeasures fontMeasures;
      private readonly VirtualDataSource listDatasource;
      private readonly Encoding[] encodings;
      private ParserNode<SearchContext> lastQuery;

      public FormMain()
      {
         InitializeComponent();
         this.Text = Globals.TITLE;

         synchronizationContext = SynchronizationContext.Current;
         encodings = new Encoding[6];
         encodings[0] = Encoding.UTF8;
         encodings[1] = Encoding.Unicode;
         encodings[2] = Encoding.BigEndianUnicode;
         encodings[3] = Encoding.Default;
         encodings[4] = new Utf81();
         encodings[5] = new Utf82(); 

         dropdownEncoding.Items.Clear();
         dropdownEncoding.Items.Add("Utf8");
         dropdownEncoding.Items.Add("Utf16");
         dropdownEncoding.Items.Add("Utf16BE");
         dropdownEncoding.Items.Add("Windows");

         if (Globals.IsDebug)
         {
            dropdownEncoding.Items.Add("Utf8 with arr");
            dropdownEncoding.Items.Add("Utf8 with ptrs");
         }
         dropdownEncoding.SelectedIndex = 0;
         listLines.VirtualListDataSource = listDatasource = new VirtualDataSource(listLines);

         searchboxDriver = new SearchHistory(cbSearch);
         btnResetSearch.Visible = Globals.IsDebug;
         btnWarning.Visible = false;

         olvcLineNumber.AutoResize(ColumnHeaderAutoResizeStyle.None);
         olvcText.AutoResize(ColumnHeaderAutoResizeStyle.None);
         cbZipEntries.Visible = false;

         prevGoto = -1;
      }

      private Encoding getCurrentEncoding()
      {
         int sel = dropdownEncoding.SelectedIndex;
         //logger.Log("Selected encoding idx={0}", sel);
         if (sel < 0) sel = 0;
         return encodings[sel];
      }

      FileHistory fileHistory, directoryHistory;

      ToolStripToolTipHelper tooltipHelper;
      private void FormMain_Load(object sender, EventArgs e)
      {
         Bitmanager.Core.GlobalExceptionHandler.HookGlobalExceptionHandler();
         GCSettings.LatencyMode = GCLatencyMode.Batch;
         this.settings = new Settings(true);
         this.fileHistory = new FileHistory("fh_");
         this.directoryHistory = new FileHistory("dh_");
         createRecentItems();

         this.olvcLineNumber.AspectGetter = getLineNumber;
         this.olvcText.AspectGetter = getLimitedLine;
         this.dropdownEncoding.SelectedIndex = 0;

         menuFileClose.Enabled = false;
         listLines.Dock = DockStyle.Fill;
         listLines.Visible = true;
         fontMeasures = new FixedFontMeasures(listLines.Font);
         if (Globals.IsDebug)
         {
            String fn = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            fn = IOUtils.FindFileToRoot (fn, @"UnitTests\data\test.txt", FindToTootFlags.Except);
            LoadFile(fn);
         }
         else
            toolStripButton2.Visible = false;

         var sb = new StringBuilder();
         sb.Append("You can enter boolean expressions (AND, OR, NOT) in the search bar.");
         sb.Append("\nAlso, you can specify search-types by using : like cs:Paris, to do a case sensitive search for Paris");
         sb.Append("\nFollowing types are supported:");
         sb.Append("\n- cs: for case-sensitive search (default is non case sensitive)");
         sb.Append("\n- csr: for case-sensitive regex search");
         sb.Append("\n- r: for case-insensitive regex search");
         sb.Append("\n\nExample: \"with blank\" AND r:en$");
         sb.Append("\nRedoing searches for previous parts are extremely fast.");
         sb.Append("\nCancel by <esc> or clicking in progress bar");

         btnSearch.ToolTipText = sb.ToString();
         cbSearch.ToolTipText = btnSearch.ToolTipText;

         tooltipHelper = new ToolStripToolTipHelper(this.toolStrip, cbSearch);
         tooltipHelper.ToolTipInterval = 20000;
         tooltipHelper.Tooltip.ReshowDelay = 4000;

         checkWarnings();

         int left, top, width, height;
         Settings.LoadFormPosition(out left, out top, out width, out height);
         if (left > 0) Left = left;
         if (top > 0) Top = top;
         if (width > 300) Width = width;
         if (height > 200) Height = height;

         logger.Log("cmdline=" + Environment.CommandLine);
         var lexer = new Lexer(Environment.CommandLine);
         for (int i=0; i<2; i++)
         {
            var x = lexer.NextToken();
            if (x == null) break;
            if (i==1 && x.Type== Lexer.TokenType.Value)
            {
               String startFile = x.Text;
               if (File.Exists(startFile)) LoadFile(startFile);
               else if (Directory.Exists(startFile)) ShowOpenDialogAndLoad(startFile);
               break;
            }
         }
      }

      private void checkWarnings()
      {
         var sb = new StringBuilder();
         if (String.IsNullOrEmpty(settings.GzipExe))
         {
            sb.Append("\n\nLoading .gz files via gzip is disabled since it is not specified in the settings");
            sb.Append("\n.gz files will be loaded via the slower SharpZLib.");
         }
         else
         {
            if (!File.Exists (settings.GzipExe))
            {
               sb.Append("\n\nLoading .gz files via gzip is disabled since gzip.exe is not found.");
               sb.Append("\n.gz files will be loaded via the slower SharpZLib.");
            }
         }
         if (!Globals.CanCompress)
         {
            sb.Append("\n\nMemory compression is disabled because bmucore_XX.dll is not found or too old.");
            sb.AppendFormat("\nVersion of {0} is {1}.", Globals.UCoreDll, Globals.UCoreDllVersion);
         }

         if (sb.Length==0)
         {
            btnWarning.Visible = false;
            return;
         }

         String msg = sb.ToString(2, sb.Length - 2);
         btnWarning.ToolTipText = msg;
         btnWarning.Visible = true;
         btnWarning.AutoToolTip = false;
      }

      private void btnWarning_Click(object sender, EventArgs e)
      {
         MessageBox.Show(btnWarning.ToolTipText, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }


      private bool saveError;
      private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
      {
         logger.Log("Initiating close");
         if (!saveError)
         {
            saveError = true;
            settings.Save();
            Settings.SaveFormPosition(Left, Top, Width, Height);
            fileHistory.Save();
            directoryHistory.Save();
         }
      }

      private void FormMain_FormClosed(object sender, FormClosedEventArgs e)
      {
         if (lf != null) lf.Dispose();
      }


      private void FormMain_DragDrop(object sender, DragEventArgs e)
      {
         if (processing) return;

         string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
         switch (files.Length)
         {
            case 0: return;
            case 1:
               LoadFile(files[0]);
               break;
            default:
               throw new Exception("Only one file can be processed at one time");
         }
      }

      private void listLines_DragDrop(object sender, DragEventArgs e)
      {
         FormMain_DragDrop(sender, e);
      }


      private void FormMain_DragEnter(object sender, DragEventArgs e)
      {
         if (processing) return;
         if (e.Data.GetDataPresent(DataFormats.FileDrop))
         {
            e.Effect = DragDropEffects.Copy;
         }
         else
         {
            e.Effect = DragDropEffects.None;
         }
      }

      private void listLines_DragEnter(object sender, DragEventArgs e)
      {
         FormMain_DragEnter(sender, e);
      }

      private void createRecentItems()
      {
         createRecentItems(fileHistory, menuRecentFiles);
         createRecentItems(directoryHistory, menuRecentFolders);
      }
         
      private void createRecentItems(FileHistory hist, ToolStripMenuItem menuItem)
      {
         String[] history = hist.Items;

         var subItems = new List<ToolStripMenuItem>();
         foreach (var x in history)
         {
            if (x == null) break;
            var subItem = new ToolStripMenuItem();
            subItem.Text = x;
            subItem.AutoSize = true;
            subItem.Click += recentFile_Click;
            subItems.Add(subItem);
         }
         menuItem.DropDownItems.Clear();
         if (subItems.Count > 0) menuItem.DropDownItems.AddRange(subItems.ToArray());
      }

      private void recentFile_Click(object sender, EventArgs e)
      {
         String fn = ((ToolStripMenuItem)sender).Text;
         if (!String.IsNullOrEmpty(fn))
         {
            if (Directory.Exists(fn))
               ShowOpenDialogAndLoad(fn);
            else
               LoadFile(fn);
         }
      }

      /// <summary>
      /// Creates a LogFile object and let it asynchronously load the file
      /// </summary>
      private void LoadFile(string filePath)
      {
         prevGoto = -1;
         fileHistory.Add(filePath);
         directoryHistory.Add(Path.GetDirectoryName(filePath));
         createRecentItems();
         indicateProcessing();

         // Clear any existing filters/reset values
         clearAll();

         this.Text = String.Format("[{0}] - {1}", filePath, Globals.TITLE);

         statusLabelMain.Text = "Loading...";
         statusLabelSearch.Text = String.Empty;
         new LogFile(this, settings, getCurrentEncoding()).Load(filePath, cancellationTokenSource.Token);
      }

      private void SearchFile()
      {
         if (lf == null) return;

         lastQuery = searchboxDriver.GetParsedQuery();
         if (lastQuery == null) return;

         statusLabelSearch.Text = "Searching...";

         indicateProcessing();
         lf.SyncSettings(settings);
         lf.Search(lastQuery, cancellationTokenSource.Token);
      }

      private void Export(string filePath)
      {
         indicateProcessing();

         statusLabelSearch.Text = "Exporting...";
         if (listLines.ModelFilter == null)
         {
            lf.Export(filePath, cancellationTokenSource.Token);
         }
         else
         {
            lf.Export(listLines.FilteredObjects, filePath, cancellationTokenSource.Token);
         }
      }

      private void ExportSelected(string filePath)
      {
         indicateProcessing();

         statusLabelSearch.Text = "Exporting...";
         lf.Export(listLines.SelectedObjects, filePath, cancellationTokenSource.Token);
      }


      int neededTextLength;

      private void computeNeededTextLength()
      {
         if (fontMeasures == null) return;
         int needed = listLines.LowLevelScrollPosition.X + listLines.Width - olvcLineNumber.Width;

         neededTextLength = fontMeasures.GetTextLengthForPixels(needed);
         //int pixels = fontMeasures.GetTextPixels(neededTextLength);
         //logger.Log("(Re)size... Needed pixels is {0}, chrs={1}, pixels for these chars is: {2}", needed, neededTextLength, pixels);
      }

      private String getLimitedLine(Object model)
      {
         String x = model == null ? String.Empty : lf.GetPartialLine((int)model, neededTextLength, replaceTabs);
         //logger.Log("Needed={0}, returned={1}", neededTextLength, x.Length);
         return x.Length > neededTextLength + 32 ? x.Substring(0, neededTextLength) : x;
      }
      private static void replaceTabs(char[] arr, int len)
      {
         for (int i = 0; i < len; i++)
         {
            if (arr[i] == '\t') arr[i] = (char)0x279c; // 0x21e5;// 0xbb;
         }
      }

      private String getLine(Object model)
      {
         String x = model == null ? String.Empty : lf.GetPartialLine((int)model);
         return x;
      }
      private String getLineNumber(Object model)
      {
         var str = String.Empty;
         if (model != null)
         {
            int ix = lf.GetOptRealLineNumber((int)model);
            if (ix >= 0) str = ix.ToString();
         }
         return str;
      }



      /// <summary>
      /// Determines how a row(item) in the listview is formatted
      /// </summary>
      private void listLines_FormatRow(object sender, BrightIdeasSoftware.FormatRowEventArgs e)
      {
         if (e.Model == null) return;

         LineFlags flags = lf.GetLineFlags((int)e.Model);
         if ((flags & LineFlags.Match) != 0)
         {
            e.Item.BackColor = settings.HighlightColor;
         }
         //PW
         //else if ((flags & LineFlags.Context) != 0)
         //{
         //   e.Item.BackColor = settings.ContextColor;
         //}
      }

      FormLine lineForm;
      private void listLines_ItemActivate(object sender, EventArgs e)
      {
         if (listLines.SelectedObjects.Count != 1) return;

         Object m = listLines.SelectedObjects[0];
         if (m == null) return;

         FormLine fl;
         if ((Control.ModifierKeys & Keys.Alt) != 0)
            fl = new FormLine();
         else
         {
            fl = lineForm;
            if (fl == null || fl.IsClosed) fl = lineForm = new FormLine();
         }
         fl.ShowLine(this.settings, lf, listDatasource.Filter, (int)m, lastQuery);
      }


      private void contextMenuExportAll_Click(object sender, EventArgs e)
      {
         SaveFileDialog sfd = new SaveFileDialog();
         sfd.Filter = "All Files|*.*";
         sfd.FileName = "*.*";
         sfd.Title = "Select export file";

         if (sfd.ShowDialog(this) == DialogResult.Cancel)
         {
            return;
         }

         Export(sfd.FileName);
      }

      private void contextMenuExportSelected_Click(object sender, EventArgs e)
      {
         SaveFileDialog sfd = new SaveFileDialog();
         sfd.Filter = "All Files|*.*";
         sfd.FileName = "*.*";
         sfd.Title = "Select export file";

         if (sfd.ShowDialog(this) == DialogResult.Cancel)
         {
            return;
         }

         ExportSelected(sfd.FileName);
      }

      private void contextMenuCopy_Click(object sender, EventArgs e)
      {
         StringBuilder sb = new StringBuilder();
         foreach (var item in listLines.SelectedObjects)
         {
            sb.AppendLine(getLine(item));
         }
         Clipboard.SetText(sb.ToString());
      }


      private void contextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
      {
         if (listLines.SelectedObjects.Count > this.settings.MultiSelectLimit)
         {
            contextMenuCopy.Enabled = false;
            exportSelectedToolStripMenuItem.Enabled = false;
            return;
         }

         contextMenuCopy.Enabled = true;
         exportSelectedToolStripMenuItem.Enabled = true;
      }


      private void toolButtonSearch_Click(object sender, EventArgs e)
      {
         if (dropdownEncoding.SelectedIndex == -1)
         {
            dropdownEncoding.Select();
            throw new Exception("The encoding is not selected");
         }
         SearchFile();
      }

      private void menuFileOpen_Click(object sender, EventArgs e)
      {
         ShowOpenDialogAndLoad(null);
      }
      private void ShowOpenDialogAndLoad (String initialDir)
      {
         OpenFileDialog openFileDialog = new OpenFileDialog();
         openFileDialog.Filter = "All Files|*.*";
         openFileDialog.FileName = "*.*";
         openFileDialog.Title = "Select file to view";

         if (initialDir != null)
            openFileDialog.InitialDirectory = initialDir;
         else
         {
            var items = fileHistory.Items;
            if (items.Length>0)
            {
               String top = items[0];
               if (!String.IsNullOrEmpty(top))
                  openFileDialog.InitialDirectory = Path.GetDirectoryName(top);
            }
         }

         if (openFileDialog.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
            LoadFile(openFileDialog.FileName);
      }

      private void clearAll()
      {
         searchboxDriver.Clear();
         lastQuery = null;
         this.listDatasource.Clear();
      }

      /// <summary>
      /// Close the resources used for opening and processing the log file
      /// </summary>
      private void menuFileClose_Click(object sender, EventArgs e)
      {
         this.Text = Globals.TITLE;

         // Clear any existing filters/reset values
         clearAll();
         listLines.ClearObjects();
         listDatasource.SetContent(0);

         setLogFile(null);

         menuFileClose.Enabled = false;
         statusLabelMain.Text = "";
         statusLabelSearch.Text = "";
      }

      /// <summary>
      /// Exits the application
      /// </summary>
      private void menuFileExit_Click(object sender, EventArgs e)
      {
         Application.Exit();
      }

      private void menuHelpHelp_Click(object sender, EventArgs e)
      {
         String fn = IOUtils.FindFileToRoot(Globals.LoadDir, "help.html", FindToTootFlags.ReturnNull);
         if (fn != null)
            Process.Start(fn);
      }

      private void menuHelpAbout_Click(object sender, EventArgs e)
      {
         using (FormAbout f = new FormAbout())
         {
            f.ShowDialog(this);
         }
      }


      private void menuToolsConfiguration_Click(object sender, EventArgs e)
      {
         using (FormSettings f = new FormSettings(this.settings))
         {
            f.ShowDialog(this);
            checkWarnings();
         }
      }


      private void indicateProcessing()
      {
         menuFileOpen.Enabled = false;
         menuFileExit.Enabled = false;
         btnSearch.Enabled = false;
         processing = true;
         statusProgress.Value = 0;
         setHourGlass();
         cancellationTokenSource = new CancellationTokenSource();
      }
      private void indicateFinished()
      {
         menuFileOpen.Enabled = true;
         menuFileExit.Enabled = true;
         btnSearch.Enabled = true;
         processing = false;
         statusProgress.Value = 0;
         clrHourGlass();
         Utils.FreeAndNil (ref cancellationTokenSource);
      }

      private void cancel()
      {
         if (cancellationTokenSource != null)
         {
            logger.Log("Cancelling");
            this.cancellationTokenSource.Cancel();
         }
      }

      private void statusProgress_Click(object sender, EventArgs e)
      {
         cancel();
      }

      private void listLines_Scroll(object sender, ScrollEventArgs e)
      {
         if (e.ScrollOrientation == ScrollOrientation.HorizontalScroll)
            this.computeNeededTextLength();
      }
      private void listLines_Resize(object sender, EventArgs e)
      {
         this.computeNeededTextLength();
      }


      void ILogFileCallback.OnSearchComplete(SearchResult result)
      {
         synchronizationContext.Post(new SendOrPostCallback(o =>
         {
            listLines.Refresh();
            indicateFinished();

            result.ThrowIfError();
            int all = result.LogFile.PartialLineCount;
            int matched = result.NumMatches;
            int perc = all == 0 ? 0 : (int)(0.5 + 100.0 * matched / all);
            var msg = String.Format("Matched {0} / {1} lines ({2}%, Search Terms: {3}),  # Duration: {4}",
                   matched,
                   all,
                   perc,
                   result.NumSearchTerms,
                   Pretty.PrintElapsedMs((int)result.Duration.TotalMilliseconds)
            );
            statusLabelSearch.Text = msg;
            handleViewSelection();
         }), null);
      }

      void ILogFileCallback.OnSearchPartial(LogFile lf, int firstMatch)
      {
         logger.Log("OnSearchPartial (first={0})", firstMatch);
         synchronizationContext.Post(new SendOrPostCallback(o =>
         {
            if (this.listDatasource.Filter != null)
            {
               firstMatch = this.listDatasource.Filter.IndexOf(firstMatch);
            }
            if (firstMatch >= 0)
               gotoAndSelectLogicalLineIndex(firstMatch);
         }), null);
      }

      private void setLogFile (LogFile newLF)
      {
         if (newLF != null)
            newLF.SetEncoding(getCurrentEncoding());
         else
            listDatasource.SetContent(0);

         if (lf != null && lf != newLF) lf.Dispose();
         lf = newLF;
         if (newLF == null)
         {
            cbZipEntries.Visible = false;
            return;
         }

         if (newLF.PartialLineCount > 0)
         {
            olvcLineNumber.Width = 20 + fontMeasures.GetTextPixels(newLF.PartialLineCount.ToString(), olvcLineNumber.Text);

            int largestIndex = newLF.LongestPartialIndex;
            String largestLine = getLine(largestIndex);
            int w = fontMeasures.GetTextPixels(largestLine, 20);
            int min = listLines.Width - olvcLineNumber.Width - 20 - listLines.Margin.Horizontal;
            logger.Log("-- w={0}, min={1}", w, min);
            if (w < min) w = min;
            olvcText.Width = w;
            
            logger.Log("-- new width={0}", olvcText.Width);
            computeNeededTextLength();
            logger.Log("-- largest partial: {0} at {1}, largest line at {2}",
               largestLine.Length,
               largestIndex,
               newLF.LongestLineIndex);
            logger.Log("-- Max width is {0} pixels, pixels in screen is {1}", w, listLines.LowLevelScrollPosition.X + listLines.Width - olvcLineNumber.Width);
         }
         listDatasource.SetContent(newLF.PartialLineCount);

         if (lf.ZipEntries==null || lf.ZipEntries.Count==0)
         {
            cbZipEntries.Visible = false;
            cbZipEntries.Items.Clear();
         }
         else
         {
            cbZipEntries.Items.Clear();
            foreach (var e in lf.ZipEntries) cbZipEntries.Items.Add(e);

            cbZipEntries.SelectedIndex = lf.ZipEntries.SelectedEntry;
            cbZipEntries.Visible = true;
         }
      }

      void ILogFileCallback.OnLoadComplete(Result result)
      {
         synchronizationContext.Post(new SendOrPostCallback(o =>
         {
            setLogFile (result.LogFile);

            indicateFinished();
            statusLabelSearch.Text = "";
            menuFileClose.Enabled = true;

            String part1 = String.Format("{0} lines / {1}", lf.PartialLineCount, Pretty.PrintSize(lf.Size));

            if (result.Error != null)
            {
               statusLabelMain.Text = part1 + " [ERROR]";
               result.ThrowIfError();
            }
            else
            {
               String duration = Pretty.PrintElapsedMs((int)result.Duration.TotalMilliseconds);
               statusLabelMain.Text = part1 + ", # Duration: " + duration;
            }

         }), null);
      }

      void ILogFileCallback.OnLoadCompletePartial(LogFile cloned)
      {
         synchronizationContext.Post(new SendOrPostCallback(o =>
         {
            logger.Log(); //Separate by empty line
            setLogFile(cloned);
            statusLabelMain.Text = String.Format("Loading...  {0} lines / {1} so far.", cloned.PartialLineCount, Pretty.PrintSize(cloned.Size));
         }), null);
      }

      void ILogFileCallback.OnExportComplete(Result result)
      {
         throw new NotImplementedException();
      }

      public void OnProgress(LogFile lf, int percent)
      {
         synchronizationContext.Post(new SendOrPostCallback(o =>
         {
            statusProgress.Value = percent;
         }), null);
      }


      private void handleViewSelection()
      {
         if (lf == null || lf.PartialLineCount == 0) return;
         if (menuViewAll.Checked)
         {
            listDatasource.SetContent(lf.PartialLineCount);
            return;
         }
         if (menuViewMatched.Checked)
         {
            listDatasource.SetContent(lf.GetMatchedList(settings.NumContextLines));
            return;
         }
         listDatasource.SetContent(lf.GetUnmatchedList(settings.NumContextLines));
         return;
      }
      private void menuView_Click(object sender, EventArgs e)
      {
         var item = sender as ToolStripMenuItem;
         var owner = item.Owner;
         foreach (ToolStripMenuItem x in owner.Items) x.Checked = x == item;

         handleViewSelection();
      }

      private void gotoToolStripMenuItem_Click(object sender, EventArgs e)
      {
         if (lf == null) return;
         gotoDialog();
      }

      private void gotoDialog()
      {
         using (FormGoToLine f = new FormGoToLine(prevGoto))
         {
            if (f.ShowDialog(this) == DialogResult.OK)
               gotoLine(prevGoto = f.LineNumber);
         }
      }
      private void gotoLine(int line)
      {
         listLines.TopItemIndex = 0;
         if (lf == null) return;
         if (line < 0) line = 0;

         int index;
         var filter = listDatasource.Filter;
         var nextLine = lf.NextLineNumber(line - 1, listDatasource.Filter, out index);
         logger.Log("Goto line={0}, filter={1}, index={2}, found line={3}, linefrompart={4}",
             line,
             filter == null ? 0 : 1,
             index,
             nextLine,
             lf.LineNumberFromPartial(index));
         gotoAndSelectLogicalLineIndex(index);
      }

      private void gotoAndSelectLogicalLineIndex(int index)
      {
         if (index < 0 || index >= listLines.GetItemCount()) return;
         listLines.SelectedIndex = index;
         listLines.EnsureVisible(index);
         listLines.Update();
      }

      private void FormMain_KeyUp(object sender, KeyEventArgs e)
      {
         if (e.Control)
         {
            switch (e.KeyCode)
            {
               default: return;
               //case Keys.C:
               //    logger.Log("Cancelling");
               //    this.cancellationTokenSource.Cancel();
               //    break;
               case Keys.G:
                  gotoToolStripMenuItem_Click(this, null); break;
               case Keys.Home:
                  gotoLine(0); break;
               case Keys.End:
                  gotoLine(int.MaxValue); break;
               case Keys.F3:
                  gotoPrevHit(); break;
            }
            e.Handled = true;
            return;
         }

         if (e.Alt || e.Shift) return;
         switch (e.KeyCode)
         {
            default: return;
            case Keys.Enter:
               logger.Log("enter from {0}", sender);
               return;
            case Keys.F3:
               gotoNextHit(); break;
         }
         e.Handled = true;
      }

      private void gotoNextHit()
      {
         if (lf == null) return;
         if (menuViewUnmatched.Selected)
            throw new Exception("Goto prev/next hit impossible if filtered for unmatched.");

         listLines.TopItemIndex = 0;
         var filter = listDatasource.Filter;
         int index = listLines.SelectedIndex;
         logger.Log("gotoNextHit (index={0}, filter={1})", index, filter == null ? 0 : 1);
         if (index >= 0)
         {
            if (filter != null) index = listDatasource.Filter[index];
         }

         index = lf.NextPartialHit(index);
         if (filter == null) goto DO_POSITION;
         int i = 0;
         for (; i < filter.Count; i++)
         {
            if (filter[i] >= index) break;
         }
         index = i;

         DO_POSITION:
         gotoAndSelectLogicalLineIndex(index);
      }

      private void gotoPrevHit()
      {
         if (lf == null) return;
         if (menuViewUnmatched.Selected)
            throw new Exception("Goto prev/next hit impossible if filtered for unmatched.");

         listLines.TopItemIndex = 0;
         var filter = listDatasource.Filter;
         int index = listLines.SelectedIndex;
         if (index >= 0)
         {
            if (filter != null) index = listDatasource.Filter[index];
         }

         index = lf.PrevPartialHit(index);
         if (filter == null) goto DO_POSITION;
         int i = 0;
         for (; i < filter.Count; i++)
         {
            if (filter[i] > index) break;
         }
         index = i - 1;

         DO_POSITION:
         gotoAndSelectLogicalLineIndex(index);
      }


      private void FormMain_KeyPress(object sender, KeyPressEventArgs e)
      {
         logger.Log("Keypress char={0} send={1}", (int)e.KeyChar, sender.GetType().Name);
         if (sender is TextBox || sender is ComboBox) return;
         switch (e.KeyChar)
         {
            default: return;
            case (char)27: //escape
               cancel();
               break;

            case '/':
               gotoNextHit(); break;
            case '?':
               gotoPrevHit(); break;
            case '<':
               gotoLine(0); break;
            case '>':
               gotoLine(int.MaxValue); break;
         }
         e.Handled = true;
      }

      private void setHourGlass()
      {
         Cursor.Current = Cursors.WaitCursor;
         UseWaitCursor = true;
      }
      private void clrHourGlass()
      {
         Cursor.Current = Cursors.Default;
         UseWaitCursor = false;
      }

      private void cbSearch_KeyUp(object sender, KeyEventArgs e)
      {
         if (e.KeyValue != 13) return;
         if (e.Alt || e.Control || e.Shift) return;
         toolButtonSearch_Click(btnSearch, null);
         listLines.Focus();

      }
      private void cbSearch_KeyPress(object sender, KeyPressEventArgs e)
      {
         if (e.KeyChar == '\r')
         {
            toolButtonSearch_Click(btnSearch, null);
            listLines.Focus();
         }
      }


      private unsafe void toolStripButton2_Click(object sender, EventArgs e)
      {
         var corehelper = new Bitmanager.UCore.CoreHelper();
         var compressor = corehelper.CreateCompressor() as ICompressor2;

         byte[] content = File.ReadAllBytes(@"E:\repos\LogViewer\LogViewerTests\data\logfile.txt");
         byte[] comp = new byte[content.Length];
         byte[] uncomp = new byte[content.Length];

         int dstlen = comp.Length;
         int srclen = dstlen;

         fixed (byte* ps = &content[0])
         fixed (byte* pd = &comp[0])
         {
            DebugOutput.Write(String.Format("pContent={0}", (long)ps));
            DebugOutput.Write(String.Format("comp={0}", (long)pd));
            dstlen = compressor.CompressLZ4(ps, pd, srclen, dstlen);
         }

         //         dstlen = compressor.CompressLZ4(ref content[0], ref comp[0], srclen, dstlen);
         logger.Log("compress ({0}) => {1}", srclen, dstlen);

         int len = compressor.DecompressLZ4(ref comp[0], ref uncomp[0], dstlen, srclen);
         logger.Log("decompress ({0}) => {1}", dstlen, len);
      }

      private void dropdownEncoding_SelectedIndexChanged(object sender, EventArgs e)
      {
         if (lf != null) lf.SetEncoding (getCurrentEncoding());
      }

      private void btnResetSearch_Click(object sender, EventArgs e)
      {
         searchboxDriver.Clear();
      }
   }

   [ComVisible(true)]
   [Guid("9616113D-27B9-4FF7-84B5-1B8542C8A5C9")]
   [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
   [SuppressUnmanagedCodeSecurity]
   [TypeLibType(TypeLibTypeFlags.FNonExtensible)]
   public interface ICompressor2
   {
      unsafe int CompressLZ4(byte* src, byte* dst, int srclen, int dstlen);
      int CompressLZ4HC(ref byte src, ref byte dst, int srclen, int dstlen);
      int DecompressLZ4(ref byte src, ref byte dst, int srclen, int dstlen);
   }


}
