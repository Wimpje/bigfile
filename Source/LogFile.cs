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

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bitmanager.Core;
using Bitmanager.IO;
using Bitmanager.Query;
using Bitmanager.BigFile.Query;

namespace Bitmanager.BigFile
{
   [Flags]
   public enum LineFlags
   {
      None = 0,
      Match = 1 << 0,
      Continuation = 1 << 1,
      AllEvaluated = 1 << 2,
      Mask0 = 1 << 3,
   };

   public interface ILogFileCallback
   {
      void OnSearchComplete(SearchResult result);
      void OnSearchPartial(LogFile lf, int firstMatch);
      void OnLoadComplete(Result result);
      void OnLoadCompletePartial(LogFile cloned);
      void OnExportComplete(Result result);
      void OnProgress(LogFile lf, int percent);
   }
   /// <summary>
   /// LogFile is responsible for loading the data and splitting it into lines.
   /// </summary>
   public class LogFile
   {
      static readonly Logger logger = Globals.MainLogger.Clone ("logfile");
      public const int FLAGS_SHIFT = 24; //Doublecheck with LineFlags!
      public const int FLAGS_MASK = (1 << 24) - 1;
      public const int MAX_NUM_MASKS = 21;

      private ThreadContext threadCtx;
      private readonly List<long> partialLines;
      private List<int> lines;
      public int LongestPartialIndex { get; private set; }
      public int LongestLineIndex { get; private set; }
      public int PartialLineCount { get { return partialLines.Count - 1; } }
      public long Size {  get { return partialLines[partialLines.Count - 1] >> FLAGS_SHIFT; } }
      public int LineCount { get { return lines == null ? partialLines.Count - 1 : lines.Count - 1; } }
      public String FileName { get { return fileName; } }
      private Encoding encoding = Encoding.UTF8;

      public IDirectStream DirectStream { get { return threadCtx == null ? null : threadCtx.DirectStream; } }

      public Encoding SetEncoding(Encoding c)
      {
         var old = encoding;
         encoding = c != null ? c : Encoding.UTF8;
         if (threadCtx != null) threadCtx.Encoding = encoding;
         return old;
      }

      public void SyncSettings (Settings settings)
      {
         loadInMemoryIfBigger = Settings.GetActualSize(settings.LoadMemoryIfBigger, "0");
         compressIfBigger = Globals.CanCompress 
            ? Settings.GetActualSize(settings.CompressMemoryIfBigger, "1GB")
            : long.MaxValue;
         searchThreads = settings.GetActualNumSearchThreads();
      }

      private String fileName;
      private readonly ILogFileCallback cb;

      #region reflected_settings
      private readonly String gzipExe;
      private readonly int MaxPartialSize;
      private int searchThreads;
      private long loadInMemoryIfBigger;
      private long compressIfBigger;
      #endregion

      private bool disposed;
      private bool partialsEncountered;

      public LogFile(ILogFileCallback cb, Settings settings, Encoding enc = null)
      {
         gzipExe = settings.GzipExe;
         MaxPartialSize = settings.MaxPartialSize <= 0 ? int.MaxValue : settings.MaxPartialSize;
         SyncSettings(settings);
         this.cb = cb;
         partialLines = new List<long>();
         SetEncoding(enc);
      }
      private LogFile(LogFile other)
      {
         this.cb = other.cb;
         this.gzipExe = other.gzipExe;
         this.loadInMemoryIfBigger = other.loadInMemoryIfBigger;
         this.compressIfBigger = other.compressIfBigger;
         this.searchThreads = other.searchThreads;

         this.fileName = other.fileName;
         this.partialsEncountered = other.partialsEncountered;
         this.partialLines = new List<long>(other.partialLines);
         this.MaxPartialSize = other.MaxPartialSize;
         this.encoding = other.encoding;
         int maxBufferSize = finalizeAdministration();

         threadCtx = other.threadCtx.NewInstanceForThread(maxBufferSize);
      }


      /// <summary>
      /// Given a line number, get the next line number. 
      /// If the line is beyond the end, LineCount is returned.
      /// If a partialFilter is supplied, only lines within that filter are used.
      /// </summary>
      public int NextLineNumber(int line, List<int> partialFilter)
      {
         if (line < -1) line = -1;
         int lineCount = LineCount;
         logger.Log("nextLineNumber ({0})", line, lineCount);
         ++line;
         if (line >= lineCount) return int.MaxValue;
         if (partialFilter == null) return line;


         int partialIndex = LineNumberToPartial(line);
         logger.Log("-- filter was set line {0}->{1}", line, partialIndex);
         int i = -1;
         int j = partialFilter.Count;
         while (j - i > 1)
         {
            int m = (i + j) / 2;
            if (partialFilter[m] >= partialIndex) j = m; else i = m;
         }
         logger.Log("-- finding in filter: i={0}, j={1}, cnt={2}", i, j, partialFilter.Count);
         if (j >= partialFilter.Count)
            logger.Log("-- at end");
         else
            logger.Log("-- next partial: {0} line: {1}", partialFilter[j], LineNumberFromPartial(partialFilter[j]));

         dumpFilter(partialFilter, i);
         return j >= partialFilter.Count ? int.MaxValue : LineNumberFromPartial(partialFilter[j]);
      }

      /// <summary>
      /// Given a line number, get the next line number. 
      /// If the line is beyond the end, LineCount is returned.
      /// If a partialFilter is supplied, only lines within that filter are used.
      /// Also return the logical index of that line.
      /// </summary>
      public int NextLineNumber(int line, List<int> partialFilter, out int logicalIndex)
      {
         int lineCount = LineCount;
         logger.Log("nextLineNumber ({0})", line, lineCount);
         ++line;

         if (line >= lineCount)
         {
            logicalIndex = PartialLineCount-1;
            return lineCount-1;
         }
         if (partialFilter == null)
         {
            logicalIndex = LineNumberToPartial(line);
            return line;
         }


         int partialIndex = LineNumberToPartial(line);
         logger.Log("-- filter was set line {0}->{1}", line, partialIndex);
         int i = -1;
         int j = partialFilter.Count;
         while (j - i > 1)
         {
            int m = (i + j) / 2;
            if (partialFilter[m] >= partialIndex) j = m; else i = m;
         }
         logger.Log("-- finding in filter: i={0}, j={1}, cnt={2}", i, j, partialFilter.Count);
         logicalIndex = j;
         if (j >= partialFilter.Count)
            logger.Log("-- at end");
         else
            logger.Log("-- next partial: {0} line: {1}", partialFilter[j], LineNumberFromPartial(partialFilter[j]));

         dumpFilter(partialFilter, i);
         return j >= partialFilter.Count ? lineCount : LineNumberFromPartial(partialFilter[j]);
      }

      /// <summary>
      /// Given a line number, get the previous line number. 
      /// If the line is before the start, -1 is returned.
      /// If a partialFilter is supplied, only lines within that filter are used.
      /// </summary>
      public int PrevLineNumber(int line, List<int> partialFilter)
      {
         int lineCount = LineCount;
         if (line > lineCount) line = lineCount;
         logger.Log("prevLineNumber ({0})", line, lineCount);
         if (line <= 0) return -1;
         if (partialFilter == null) return line - 1;

         int partialIndex = LineNumberToPartial(line);
         logger.Log("-- filter was set line {0}->{1}", line, partialIndex);
         int i = -1;
         int j = partialFilter.Count;
         while (j - i > 1)
         {
            int m = (i + j) / 2;
            if (partialFilter[m] >= partialIndex) j = m; else i = m;
         }
         logger.Log("-- finding in filter: i={0}, j={1}, cnt={2}", i, j, partialFilter.Count);
         if (i < 0)
            logger.Log("-- at top");
         else
            logger.Log("-- next partial: {0} line: {1}", partialFilter[i], LineNumberFromPartial(partialFilter[i]));
         dumpFilter(partialFilter, i);
         return i < 0 ? -1 : LineNumberFromPartial(partialFilter[i]);
      }

      private void dumpFilter(List<int> partialFilter, int i)
      {
         int from = i - 5;
         if (from < 0) from = 0;

         int until = i + 6;
         if (until > partialFilter.Count) until = partialFilter.Count;

         for (int j = from; j < until; j++)
         {
            logger.Log("-- Filter [{0}] = {1}, {2} (real) ", j, partialFilter[j], LineNumberFromPartial(partialFilter[j]));
         }
      }

      /// <summary>
      /// Administrates the longest (partial) line and returns the buffersize that is needed to hold the max (partial)line. 
      /// </summary>
      private int finalizeAdministration()
      {
         if (partialsEncountered)
         {
            lines = new List<int>();
            lines.Add(0);
         }
         else
            lines = null;

         if (partialLines.Count == 0) partialLines.Add(0);

         long prevPartial = partialLines[0];
         long prevLine = partialLines[0];
         int maxPartialLen = 0;
         int maxPartialIdx = 0;
         int maxLineLen = 0;
         int maxLineIdx = 0;
         int len;
         const int CONTINUATION_FLAG = (int)LineFlags.Continuation;
         for (int i = 1; i < partialLines.Count; i++)
         {
            long offset = partialLines[i];
            int flags = (int)offset;
            offset >>= FLAGS_SHIFT;
            if (lines != null)
            {
               if ((flags & CONTINUATION_FLAG) == 0)
               {
                  len = (int)(offset - prevLine);
                  if (len > maxLineLen)
                  {
                     maxLineIdx = lines.Count-1;
                     maxLineLen = len;
                  }
                  prevLine = offset;
                  lines.Add(i);
               }
            }

            len = (int)(offset - prevPartial);
            prevPartial = offset;

            if (len <= maxPartialLen) continue;
            maxPartialLen = len;
            maxPartialIdx = i - 1;
         }
         LongestPartialIndex = maxPartialIdx;
         LongestLineIndex = lines != null ? maxLineIdx : maxPartialIdx;
         return Math.Max(maxPartialLen, maxLineLen);
      }

      public int NextPartialHit(int idxPartial)
      {
         if (idxPartial < -1) idxPartial = -1;
         long mask = (int)LineFlags.Match;
         for (int i = idxPartial + 1; i < partialLines.Count - 1; i++)
         {
            if ((partialLines[i] & mask) != 0) return i;
         }
         return int.MaxValue;
      }
      public int PrevPartialHit(int idxPartial)
      {
         if (idxPartial > PartialLineCount) idxPartial = PartialLineCount;
         long mask = (int)LineFlags.Match;
         for (int i = idxPartial - 1; i >= 0; i--)
         {
            if ((partialLines[i] & mask) != 0) return i;
         }
         return -1;
      }



      public List<int> GetMatchedList(int contextLines)
      {
         var ret = new List<int>();

         if (contextLines > 0)
         {
            int prevMatch = -1;
            for (int i = 0; i < partialLines.Count - 1; i++)
            {
               if ((GetLineFlags(i) & LineFlags.Match) == 0) continue;
               if (contextLines > 0)
               {
                  int endPrevContext = prevMatch + 1 + contextLines;
                  if (endPrevContext > i) endPrevContext = i;
                  int startContext = i - contextLines;
                  if (startContext <= endPrevContext)
                     startContext = endPrevContext;
                  for (int j = prevMatch + 1; j < endPrevContext; j++)
                     ret.Add(j);
                  for (int j = startContext; j < i; j++)
                     ret.Add(j);
               }
               ret.Add(i);
               prevMatch = i;
            }
            if (contextLines > 0)
            {
               int endPrevContext = prevMatch + 1 + contextLines;
               if (endPrevContext > partialLines.Count - 1) endPrevContext = partialLines.Count - 1;
               for (int j = prevMatch + 1; j < endPrevContext; j++)
                  ret.Add(j);
            }
            return ret;
         }

         //No context lines
         for (int i = 0; i < partialLines.Count - 1; i++)
         {
            if ((GetLineFlags(i) & LineFlags.Match) == 0) continue;
            ret.Add(i);
         }
         return ret;
      }

      public List<int> GetUnmatchedList(int contextLines)
      {
         var ret = new List<int>();

         if (contextLines > 0)
         {
            int prevMatch = -1;
            for (int i = 0; i < partialLines.Count - 1; i++)
            {
               if ((GetLineFlags(i) & LineFlags.Match) != 0) continue;
               if (contextLines > 0)
               {
                  int endPrevContext = prevMatch + 1 + contextLines;
                  if (endPrevContext > i) endPrevContext = i;
                  int startContext = i - contextLines;
                  if (startContext <= endPrevContext)
                     startContext = endPrevContext;
                  for (int j = prevMatch + 1; j < endPrevContext; j++)
                     ret.Add(j);
                  for (int j = startContext; j < i; j++)
                     ret.Add(j);
               }
               ret.Add(i);
               prevMatch = i;
            }
            if (contextLines > 0)
            {
               int endPrevContext = prevMatch + 1 + contextLines;
               if (endPrevContext > partialLines.Count - 1) endPrevContext = partialLines.Count - 1;
               for (int j = prevMatch + 1; j < endPrevContext; j++)
                  ret.Add(j);
            }
            return ret;
         }

         //No context lines
         for (int i = 0; i < partialLines.Count - 1; i++)
         {
            if ((GetLineFlags(i) & LineFlags.Match) != 0) continue;
            ret.Add(i);
         }
         return ret;
      }

      /// <summary>
      /// Load a zip file.
      /// This is done by unzipping it into memory and serving the UI from the memorystream
      /// Unzip is preferrable done by starting gzip, and otherwise by using sharpzlib
      /// </summary>
      private void loadZipFile(String fn, CancellationToken ct)
      {
         Stream strm = null;
         try
         {
            try
            {
               if (!String.IsNullOrEmpty(gzipExe))
               {
                  logger.Log("Try loading '{0}' via GZip.", fn);
                  strm = new GzipProcessInputStream(fn, gzipExe, Globals.MainLogger);
               }
            }
            catch (Exception e)
            {
               logger.Log("Cannot use gzip:");
               logger.Log(e);
            }

            if (strm == null)
            {
               logger.Log("Loading '{0}' via SharpZipLib.", fn);
               strm = new FileStream(fn, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
               var gz = new ICSharpCode.SharpZipLib.GZip.GZipInputStream(strm);
               gz.IsStreamOwner = true;
               strm = gz;
            }

            loadStreamIntoMemory(strm, -1, ct);
         }
         finally
         {
            Utils.FreeAndNil(ref strm);
         }
      }


      private void loadStreamIntoMemory (Stream strm, long fileLength, CancellationToken ct)
      {
         const int chunksize = 256 * 1024;
         Logger compressLogger = Globals.MainLogger.Clone("compress");
         Stream mem;
         if (this.compressIfBigger <= 0)
            mem = new CompressedChunkedMemoryStream(chunksize, compressLogger);
         else
            mem = new ChunkedMemoryStream(chunksize);

         threadCtx = new ThreadContext(encoding, mem as IDirectStream, this.partialLines);

         var tmp = new byte[64 * 1024];

         long position = 0;
         int counter = 0;
         int nextPartial = 10000;
         AddLine(0);
         bool checkCompress = true;
         while (true)
         {
            int len = strm.Read(tmp, 0, tmp.Length);
            if (len == 0) break;
            mem.Write(tmp, 0, len);

            position = addLinesForBuffer(position, tmp, len);

            if (counter++ % 100 == 0)
            {
               int x;
               if (fileLength >= 0)
                  x = perc(position, fileLength);
               else
               {
                  x = (counter / 100) % 200;
                  if (x >= 100) x = 200 - x;
               }
               if (disposed || ct.IsCancellationRequested) break;
               cb.OnProgress(this, x);

               if (checkCompress && position > compressIfBigger && mem is ChunkedMemoryStream)
               {
                  logger.Log("Switching to compressed since size {0} > {1}.", Pretty.PrintSize(position), Pretty.PrintSize(compressIfBigger));
                  checkCompress = false;
                  mem = ((ChunkedMemoryStream)mem).CreateCompressedChunkedMemoryStream(compressLogger);
                  threadCtx = new ThreadContext(encoding, mem as IDirectStream, this.partialLines);
               }

               if (partialLines.Count >= nextPartial)
               {
                  nextPartial = nextTriggerForPartialLoad();
                  threadCtx.DirectStream.PrepareForNewInstance();
                  cb.OnLoadCompletePartial(new LogFile(this));
               }
            }
         }
         long o2 = partialLines[partialLines.Count - 1] >> FLAGS_SHIFT;
         if (o2 != position)
            AddLine(position);

         var c = mem as CompressedChunkedMemoryStream;
         if (c != null)
         {
            c.FinalizeCompressor(true);
            logger.Log("File len={0}, compressed={1}, ratio={2:F1}%", c.Length, c.GetCompressedSize(), 100.0 * c.GetCompressedSize() / c.Length);
         }
      }

      private void loadNormalFile(string fn, CancellationToken ct)
      {
         byte[] tempBuffer = new byte[512 * 1024];

         var directStream = new DirectFileStreamWrapper(fn, 4096);
         var fileStream = directStream.BaseStream;

         long totalLength = fileStream.Length;

         logger.Log("Loading '{0}' via FileStream.", fn);
         if (totalLength >= this.loadInMemoryIfBigger)
         {
            logger.Log("-- Loading into memory since '{0}' > '{1}'.", Pretty.PrintSize(totalLength), Pretty.PrintSize(loadInMemoryIfBigger));
            loadStreamIntoMemory(fileStream, totalLength, ct);
            return;
         }

         this.threadCtx = new ThreadContext(encoding, directStream, partialLines);
         // Calcs and finally point the position to the end of the line
         long position = 0;
         int counter = 0;
         int nextPartial = 10000;

         AddLine(0);
         while (position < fileStream.Length)
         {
            int bytesRead = fileStream.Read(tempBuffer, 0, tempBuffer.Length);
            if (bytesRead == 0) break;

            position = addLinesForBuffer(position, tempBuffer, bytesRead);

            if (counter++ % 30 == 0)
            {
               if (ct.IsCancellationRequested || disposed) break;
               cb.OnProgress(this, perc(position, totalLength));

               if (partialLines.Count >= nextPartial)
               {
                  nextPartial = nextTriggerForPartialLoad();
                  cb.OnLoadCompletePartial(new LogFile(this));
               }
            }
         }
         long o2 = partialLines[partialLines.Count - 1] >> FLAGS_SHIFT;
         if (o2 != position)
            AddLine(position);
      }

      int nextTriggerForPartialLoad ()
      {
         return (int)(partialLines.Count * 1.5);
      }
      static int perc (long partial, long all)
      {
         if (all == 0) return 0;
         return (int)((100.0 * partial) / all);
      }
      private bool onProgress(double perc)
      {
         if (disposed) return false;
         cb.OnProgress(this, (int)perc);
         return true;
      }
      private long addLinesForBuffer(long position, byte[] buf, int count)
      {
         long prev = partialLines[partialLines.Count - 1] >> FLAGS_SHIFT;
         int lenCorrection = (int)(position - prev);
         int lastDot = -100;
         int lastComma = -100;
         int lastGT = -100;
         int lastSpace = -100;
         for (int i = 0; i < count; i++)
         {
            switch (buf[i])
            {
               case (byte)'.': lastDot = i; break;
               case (byte)',': lastComma = i; break;
               case (byte)'>': lastGT = i; break;
               case (byte)' ': lastSpace = i; break;
               case (byte)10:
                  AddLine(position + i + 1);
                  lastDot = -100;
                  lastComma = -100;
                  lastGT = -100;
                  lastSpace = -100;
                  lenCorrection = -(i + 1);
                  continue;
            }
            if (i + lenCorrection < MaxPartialSize) continue;

            int end = i;
            if (i - lastComma < 32) end = lastComma + 1;
            else if (i - lastGT < 32) end = lastGT + 1;
            else if (i - lastSpace < 32) end = lastSpace + 1;
            else if (i - lastDot < 32) end = lastDot + 1;

            AddPartialLine(position + end);
            lastDot = -100;
            lastComma = -100;
            lastGT = -100;
            lastSpace = -100;
            lenCorrection = -end;
         }
         return position + count;
      }

      /// <summary>
      /// Load a (zip) file
      /// </summary>
      public Task Load(string fn, CancellationToken ct)
      {
         return Task.Run(() =>
         {
            DateTime start = DateTime.Now;
            Exception err = null;
            partialsEncountered = false;
            this.fileName = Path.GetFullPath(fn);
            try
            {
               LongestPartialIndex = -1;
               bool dozip = String.Equals(".gz", Path.GetExtension(fileName), StringComparison.OrdinalIgnoreCase);
               if (dozip) loadZipFile(fileName, ct); else loadNormalFile(fileName, ct);
               logger.Log("-- Loaded. Size={0}, #Lines={1}", Pretty.PrintSize(GetPartialLineOffset(partialLines.Count-1)), partialLines.Count-1);
            }
            catch (Exception ex)
            {
               err = ex;
               Logs.ErrorLog.Log(ex);
            }
            finally
            {
               int maxBufferSize = finalizeAdministration();
               if (threadCtx != null) threadCtx.SetMaxBufferSize (maxBufferSize);
               if (!disposed)
               {
                  cb.OnProgress(this, 100);
                  cb.OnLoadComplete(new Result(this, start, err, ct.IsCancellationRequested));
               }
            }
         });
      }

      public void Dispose()
      {
         disposed = true;

         this.partialLines.Clear();
         this.lines = null;
         if (this.threadCtx != null)
         {
            this.threadCtx.Close();
         }
      }


      private void MarkMatch(int line, int numContextLines)
      {
         partialLines[line] = partialLines[line] | (int)LineFlags.Match;
      }
      private void ResetMatch(int line, int numContextLines)
      {
         partialLines[line] = partialLines[line] & ~(int)LineFlags.Match;
      }

      public void ResetMatches()
      {
         resetFlags((int)LineFlags.Match);
      }
      public void ResetMatchesAndFlags()
      {
         resetFlags(FLAGS_MASK & ~(int)LineFlags.Continuation);
      }
      private void resetFlags(int mask)
      {
         long notmask = ~(long)mask;
         logger.Log("resetFlags ({0:X}: not={1:X})", mask, notmask);
         for (int i = 0; i < partialLines.Count; i++)
         {
            partialLines[i] = notmask & partialLines[i];
         }
      }

      public Task Search(ParserNode<SearchContext> query, CancellationToken ct)
      {
         if (threadCtx == null) return Task.CompletedTask;
         threadCtx.DirectStream.PrepareForNewInstance();
         return Task.Run(() =>
         {
            logger.Log("Search: phase1 starting with {0} threads", searchThreads);
            logger.Log("Search: encoding: {0}", encoding);
            var ctx = new SearchContext(query);
            foreach (var x in ctx.LeafNodes)
               logger.Log("-- {0}", x);

            DateTime start = DateTime.Now;
            Exception err = null;
            int matches = 0;

            try
            {
               Task<int>[] tasks = new Task<int>[searchThreads-1];
               int N = partialLines.Count - 1;
               int M = N / searchThreads;
               for (int i=0; i<tasks.Length; i++)
               {
                  int end = N;
                  N -= M;
                  tasks[i] = Task.Run(() => _search(ctx.NewInstanceForThread(), end - M, end, ct));
               }
               int matches0 = matches = _search(ctx, 0, N, ct);
               for (int i = 0; i < tasks.Length; i++)
               {
                  matches += tasks[i].Result;
                  tasks[i].Dispose();
               }

               if (matches0 == 0 && matches > 0)
               {
                  int firstHit = this.NextPartialHit(N);
                  if (firstHit >= 0)
                     cb.OnSearchPartial(this, firstHit);
               }

               ctx.MarkComputed();
               logger.Log("Search: phase1 ended. Total #matches={0}", matches);

               DONE:;
            }
            catch (Exception e)
            {
               err = e;
            }
            finally
            {
               if (!disposed)
               {
                  cb.OnProgress(this, 100);
                  cb.OnSearchComplete(new SearchResult(this, start, err, ct.IsCancellationRequested, matches, ctx.LeafNodes.Count));
               }
            }
         });
      }

      private int _search(SearchContext ctx, int start, int end, CancellationToken ct)
      {
         const int MOD = 5000;
         int matches = 0;
         var query = ctx.Query;

         logger.Log("Search: thread start: from {0} to {1} ", start, end);
         if (ctx.NeedLine) goto NEEDLINE;
         for (ctx.Index = start; ctx.Index < end; ctx.Index++)
         {
            if (ctx.Index % MOD == 0)
            {
               if (ct.IsCancellationRequested) break;
               if (start == 0 && !onProgress((100.0 * ctx.Index) / end)) break;
            }

            ctx.SetLine(partialLines[ctx.Index], null);
            if (!query.Evaluate(ctx))
            {
               partialLines[ctx.Index] = ctx.OffsetAndFlags;
               continue;
            }

            partialLines[ctx.Index] = ctx.OffsetAndFlags | (int)LineFlags.Match;

            if (++matches == 1 && !disposed && start == 0)
               cb.OnSearchPartial(this, ctx.Index);
         }
         goto EXIT_RTN;


         NEEDLINE:;
         var threadCtx = this.threadCtx.NewInstanceForThread();
         try
         {
            for (ctx.Index = start; ctx.Index < end; ctx.Index++)
            {
               if (ctx.Index % MOD == 0)
               {
                  if (ct.IsCancellationRequested) break;
                  if (start == 0 && !onProgress((100.0 * ctx.Index) / end)) break;
               }

               int len = threadCtx.ReadPartialLineCharsInBuffer(ctx.Index, ctx.Index + 1);

               ctx.SetLine(partialLines[ctx.Index], new String(threadCtx.CharBuffer, 0, len));
               if (!query.EvaluateDeep(ctx))
               {
                  partialLines[ctx.Index] = ctx.OffsetAndFlags; ;
                  continue;
               }

               partialLines[ctx.Index] = ctx.OffsetAndFlags | (int)LineFlags.Match;

               if (++matches == 1 && !disposed && start == 0)
                  cb.OnSearchPartial(this, ctx.Index);
            }
         }
         finally
         {
            threadCtx.CloseInstance();
         }
         goto EXIT_RTN;

         EXIT_RTN:
         logger.Log("Search: thread end: from {0} to {1}: {2} out of {3}", start, end, matches, end-start);
         return matches;
      }
      private bool progressAndCheck(int index, CancellationToken ct)
      {
         if (!onProgress ((100.0*index) / partialLines.Count)) return false;
         return !ct.IsCancellationRequested;
      }

      /// <summary>
      /// 
      /// </summary>
      /// <param name="filePath"></param>
      /// <param name="ct"></param>
      public void Export(string filePath, CancellationToken ct)
      {
         this.ExportToFile(this.partialLines, filePath, ct);
      }

      /// <summary>
      /// 
      /// </summary>
      /// <param name="lines"></param>
      /// <param name="filePath"></param>
      /// <param name="ct"></param>
      public void Export(IEnumerable lines, string filePath, CancellationToken ct)
      {
         this.ExportToFile(lines, filePath, ct);
      }


      /// <summary>
      /// 
      /// </summary>
      /// <param name="filePath"></param>
      /// <param name="ct"></param>
      private void ExportToFile(IEnumerable lines, string filePath, CancellationToken ct)
      {
         Task.Run(() =>
         {
            Exception err = null;
            DateTime start = DateTime.Now;
            bool cancelled = false;
            var ctx = threadCtx.NewInstanceForThread();
            try
            {
               using (FileStream fs = new FileStream(filePath, FileMode.Create))
               {
                  byte[] endLine = new byte[2] { 13, 10 };
                  long counter = 0;
                  foreach (var ll in lines)
                  {
                     int ix = (int)ll;
                     int len = ctx.ReadPartialLineBytesInBuffer(ix, ix + 1);
                     fs.Write(ctx.ByteBuffer, 0, len);
                        // Add \r\n
                        fs.Write(endLine, 0, 2);

                     if (counter++ % 50 == 0)
                     {
                        if (!onProgress(counter / this.partialLines.Count * 100.0)) break;

                        if (ct.IsCancellationRequested)
                        {
                           cancelled = true;
                           return;
                        }
                     }
                  }

               }
            }
            catch (Exception e)
            {
               err = e;
            }
            finally
            {
               ctx.CloseInstance();
               if (onProgress(100))
                  cb.OnExportComplete(new Result(this, start, err, cancelled));
            }
         });
      }

      private void AddLine(long offset)
      {
         partialLines.Add(offset << FLAGS_SHIFT);
      }
      private void AddPartialLine(long offset)
      {
         partialLines.Add((offset << FLAGS_SHIFT) | (int)LineFlags.Continuation);
         partialsEncountered = true;
      }

      public long GetPartialLineOffset(int line)
      {
         //logger.Log("PartialLineOffset[{0}]: raw={1:X}, shift={2:X}", line, partialLines[line], (int)(partialLines[line] >> FLAGS_SHIFT));
         return (partialLines[line] >> FLAGS_SHIFT);
      }
      public long GetPartialLineOffsetAndFlags(int line)
      {
         return partialLines[line];
      }
      public int GetLineOffset(int line)
      {
         if (lines != null) line = lines[line];
         return (int)(partialLines[line] >> FLAGS_SHIFT);
      }

      public string GetPartialLine(int index, int maxChars = -1)
      {
         if (index < 0 || index >= partialLines.Count - 1) return String.Empty;
         return threadCtx.GetPartialLine(index, index + 1, maxChars);
      }
      

      public String GetLine(int index)
      {
         if (index < 0) return String.Empty;
         if (lines != null)
         {
            if (index >= lines.Count - 1) return String.Empty;
            return threadCtx.GetPartialLine(lines[index], lines[index+1], -1);
         }
         if (index >= partialLines.Count - 1) return String.Empty;
         return threadCtx.GetPartialLine(index, index + 1, -1);
      }

      public LineFlags GetLineFlags(int line)
      {
         return (LineFlags)(FLAGS_MASK & (int)partialLines[line]);
      }

      public int LineNumberToPartial(int line)
      {
         if (lines == null) return line;
         return lines[line];
      }

      public int LineNumberFromPartial(int line)
      {
         if (lines == null) return line;
         int i = -1;
         int j = lines.Count;
         while (j - i > 1)
         {
            int m = (i + j) / 2;
            if (lines[m] > line) j = m; else i = m;
         }
         return i;
      }
      public int GetOptRealLineNumber(int line)
      {
         if (lines == null) return line;
         return ((int)partialLines[line] & (int)LineFlags.Continuation) == 0 ? LineNumberFromPartial(line) : -1;
      }

      public String GetLineAndFlags(int line, out LineFlags flags)
      {
         flags = (LineFlags)(FLAGS_MASK & (int)partialLines[line]);
         return GetPartialLine(line);
      }


   }




   /// <summary>
   /// Result to be returned when a Load() is completed
   /// </summary>
   public class Result
   {
      public readonly LogFile LogFile;
      public Result(LogFile logfile, DateTime started, Exception err, bool cancelled)
      {
         this.LogFile = logfile;
         this.Duration = DateTime.Now - started;
         this.Error = err;
         this.Cancelled = cancelled;
         Logs.ErrorLog.Log("Result created with {0}", err);
      }
      public readonly TimeSpan Duration;
      public readonly Exception Error;
      public readonly bool Cancelled;

      public void ThrowIfError()
      {
         if (Error != null) throw new Exception(Error.Message, Error);
      }
   }

   /// <summary>
   /// Result to be returned when a Search() is completed
   /// </summary>
   public class SearchResult : Result
   {
      public readonly int NumMatches;
      public readonly int NumSearchTerms;
      public SearchResult(LogFile logfile, DateTime started, Exception err, bool cancelled, int matches, int numSearchTerms)
          : base(logfile, started, err, cancelled)
      {
         this.NumMatches = matches;
         this.NumSearchTerms = numSearchTerms;
      }
   }
}
