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
using System.Collections.Generic;
using System.Threading;
using Bitmanager.IO;
using Bitmanager.BigFile;
using Bitmanager.BigFile.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Bitmanager.Core;

namespace Bitmanager.BigFile
{
   [TestClass]
   public class LogFileTests: TestBase
   {
      private readonly Settings settings;
      public LogFileTests()
      {
         settings = new Settings();
         if (settings.GzipExe == null) throw new Exception("Cannot find Gzip.exe in path or app folders.");
      }

      [TestMethod]
      public void TestLoad()
      {
         _load(dataDir + "allcountries.txt.gz");  //gzip
         _load(dataDir + "allcountries.txt"); //unpacked
      }

      [TestMethod]
      public void TestZipError()
      {
         String result = "ok";
         try
         {
            _load(dataDir + "test.txt.gz");  //gzip
         }
         catch (Exception e)
         {
            result = e.Message;
         }
         if (result.IndexOf(": not in gzip format") < 0)
            throw new BMException("error should contain ': not in gzip format', but was {0}", result);
      }

      [TestMethod]
      public void TestSearch()
      {
         var cb = new CB();
         var logFile = new LogFile(cb, settings, null);
         logFile.Load(dataDir+"test.txt", CancellationToken.None).Wait();

         Assert.AreEqual(5, search(logFile, "aap"));
         Assert.AreEqual(7, search(logFile, "noot"));
         Assert.AreEqual(8, search(logFile, "mies"));
         Assert.AreEqual(2, search(logFile, "mies AND aap"));
         Assert.AreEqual(11, search(logFile, "mies OR aap"));
         Assert.AreEqual(6, search(logFile, "mies NOT aap"));


         int flagsMask = 0;
         for (int i=0; i<LogFile.MAX_NUM_MASKS; i++)
            flagsMask |= ((int)LineFlags.Mask0) << i;
         Console.WriteLine("Flags={0:X}, const={1:X}", flagsMask, LogFile.FLAGS_MASK);

         List<long> offsets = new List<long>();
         for (int i = 0; i < logFile.PartialLineCount; i++) offsets.Add(logFile.GetPartialLineOffset(i));
         logFile.ResetMatchesAndFlags();
         for (int i = 0; i < logFile.PartialLineCount; i++)
            Assert.AreEqual (offsets[i], logFile.GetPartialLineOffset(i));

         var searchNodes = new SearchNodes();
         dumpOffsets(logFile, "before search");
         dumpSearchNodes(searchNodes, "");
         Assert.AreEqual(5, search(logFile, "aap", searchNodes));
         dumpOffsets(logFile, "after aap");
         dumpSearchNodes(searchNodes, "");
         Assert.AreEqual(7, search(logFile, "noot", searchNodes));
         dumpOffsets(logFile, "after noot");
         dumpSearchNodes(searchNodes, "");
         Assert.AreEqual(8, search(logFile, "mies", searchNodes));
         dumpOffsets(logFile, "after mies");
         dumpSearchNodes(searchNodes, "");
         search(logFile, "mies AND aap", searchNodes);
         dumpOffsets(logFile, "after mies AND aap");
         dumpSearchNodes(searchNodes, "");
         Assert.AreEqual(2, search(logFile, "mies AND aap", searchNodes));
         //Assert.AreEqual(11, search(logFile, "mies OR aap", searchNodes));
         //Assert.AreEqual(6, search(logFile, "mies NOT aap", searchNodes));
         Assert.AreEqual(3, searchNodes.Count);
      }

      [TestMethod]
      public void TestCompressErrors()
      {
         var settings = new Settings();
         settings.CompressMemoryIfBigger = "0";
         settings.LoadMemoryIfBigger = "0";
         var cb = new CB();
         var logFile = new LogFile(cb, settings, null);
         logFile.Load(dataDir + "compress-errors.txt", CancellationToken.None).Wait();

         var mem = checkAndCast<CompressedChunkedMemoryStream>(logFile.DirectStream);
         Assert.AreEqual(false, mem.IsCompressionEnabled);
         logFile.Dispose();
      }

      private T checkAndCast<T> (IDirectStream strm) where T: IDirectStream
      {
         Assert.IsNotNull(strm);
         Assert.IsInstanceOfType(strm, typeof (T));
         return (T)strm;
      }



      private void dumpOffsets(LogFile lf, String why)
      {
         logger.Log();
         logger.Log("Dumping offsets and flags for {0} lines. Reason={1}", lf.PartialLineCount, why);
         for (int i = 0; i < lf.PartialLineCount; i++)
         {
            logger.Log("-- line[{0}]: 0x{1:X}", i, lf.GetPartialLineOffsetAndFlags(i));
         }
      }
      private void dumpSearchNodes(SearchNodes nodes, String why)
      {
         logger.Log();
         logger.Log("Dumping {0} searchNodes. Reason={1}", nodes.Count, why);
         int i = 0;
         foreach (var node in nodes)
         {
            logger.Log("-- node[{0}]: Bit={1}, mask={2:X}, arg={3}", i, node.BitIndex, node.BitMask, node);
            i++;
         }
      }
      private int countMatched (LogFile lf)
      {
         return lf.GetMatchedList(0).Count;
      }

      private int search (LogFile lf, String x, SearchNodes searchNodes=null)
      {
         if (searchNodes==null) searchNodes = new SearchNodes();
         lf.Search(searchNodes.Parse(x), CancellationToken.None).Wait();
         return lf.GetMatchedList(0).Count;
      }


      public void _load (String fn)
      {
         var cb = new CB();

         //Non partial
         var logFile = new LogFile(cb, settings);
         logFile.Load(fn, CancellationToken.None).Wait();
         cb.Result.ThrowIfError();

         Assert.AreEqual(18, logFile.LineCount);
         Assert.AreEqual(0, logFile.GetLineOffset(0));
         Assert.AreEqual(6914, logFile.GetLineOffset(1));
         Assert.AreEqual(13114, logFile.GetLineOffset(2));

         Assert.AreEqual(6007, logFile.GetLine(0).Length);
         Assert.AreEqual(5688, logFile.GetLine(1).Length);
         Assert.AreEqual(4757, logFile.GetLine(2).Length);

         //Checking longest line
         Assert.AreEqual(5, logFile.LongestPartialIndex);
         Assert.AreEqual(7464, logFile.GetPartialLine(logFile.LongestPartialIndex).Length);
         Assert.AreEqual(5, logFile.LongestLineIndex);
         Assert.AreEqual(7464, logFile.GetLine(logFile.LongestLineIndex).Length);


         //Partials
         settings.MaxPartialSize = 1024;
         logFile = new LogFile(cb, settings);
         settings.MaxPartialSize = -1;
         logFile.Load(fn, CancellationToken.None).Wait();
         Assert.AreEqual(18, logFile.LineCount);
         Assert.AreEqual(0, logFile.GetLineOffset(0));
         Assert.AreEqual(6914, logFile.GetLineOffset(1));
         Assert.AreEqual(13114, logFile.GetLineOffset(2));

         Assert.AreEqual(6007, logFile.GetLine(0).Length);
         Assert.AreEqual(5688, logFile.GetLine(1).Length);
         Assert.AreEqual(4757, logFile.GetLine(2).Length);

         Assert.AreEqual(90, logFile.PartialLineCount);
         Assert.AreEqual(0, logFile.GetPartialLineOffset(0));
         Assert.AreEqual(1018, logFile.GetPartialLineOffset(1));
         Assert.AreEqual(2042, logFile.GetPartialLineOffset(2));

         Assert.AreEqual(940, logFile.GetPartialLine(0).Length);
         Assert.AreEqual(965, logFile.GetPartialLine(1).Length);
         Assert.AreEqual(882, logFile.GetPartialLine(2).Length);

         //Checking longest partial line
         Assert.AreEqual(56, logFile.LongestPartialIndex);
         Assert.AreEqual(859, logFile.GetPartialLine(logFile.LongestPartialIndex).Length);

         //Checking longest  line
         Assert.AreEqual(5, logFile.LongestLineIndex);
         //Assert.AreEqual(7464, logFile.GetLine(logFile.LongestLineIndex).Length);
         Assert.AreEqual(7464, logFile.GetLine(5).Length);

         testNextLine(logFile);
         testPrevLine(logFile);

         //Match all lines
         Assert.AreEqual (logFile.PartialLineCount, search(logFile, "r:."));
         testNextPartial(logFile);
         testPrevPartial(logFile);

         //Test the next/prev by using a line filter
         var matches = new List<int>();
         matches.Add(logFile.LineNumberToPartial(3));
         matches.Add(logFile.LineNumberToPartial(5));
         logger.Log("Matching lines: {0} for line 3 and {1} for line 5", matches[0], matches[1]);
         //dumpOffsets(logFile, "test");
         Assert.AreEqual(3, logFile.NextLineNumber(-123, matches));
         Assert.AreEqual(5, logFile.NextLineNumber(3, matches));
         Assert.AreEqual(int.MaxValue, logFile.NextLineNumber(5, matches));

         Assert.AreEqual(5, logFile.PrevLineNumber(99999, matches));
         Assert.AreEqual(3, logFile.PrevLineNumber(5, matches));
         Assert.AreEqual(-1, logFile.PrevLineNumber(3, matches));
      }

      private void testNextLine(LogFile lf)
      {
         Assert.AreEqual(0, lf.NextLineNumber(-123, null));
         int line = -1;
         while (true)
         {
            int next = lf.NextLineNumber(line, null);
            logger.Log("Line={0}, next={1}", line, next);
            if (next == int.MaxValue) break;

            Assert.AreEqual(line + 1, next);
            line = next;
         }
      }
      private void testPrevLine(LogFile lf)
      {
         Assert.AreEqual(lf.LineCount-1, lf.PrevLineNumber(int.MaxValue, null));
         int line = lf.LineCount;
         while (true)
         {
            int prev = lf.PrevLineNumber(line, null);
            logger.Log("Line={0}, prev={1}", line, prev);
            if (prev < 0) break;

            Assert.AreEqual(line-1, prev);
            line = prev;
         }
      }

      private void testNextPartial(LogFile lf)
      {
         Assert.AreEqual(0, lf.NextPartialHit(-123));
         int line = -1;
         while (true)
         {
            int next = lf.NextLineNumber(line, null);
            logger.Log("Line={0}, next={1}", line, next);
            if (next == int.MaxValue) break;

            Assert.AreEqual(line + 1, next);
            line = next;
         }
      }
      private void testPrevPartial(LogFile lf)
      {
         Assert.AreEqual(lf.PartialLineCount - 1, lf.PrevPartialHit(int.MaxValue));
         int line = lf.LineCount;
         while (true)
         {
            int prev = lf.PrevLineNumber(line, null);
            logger.Log("Line={0}, prev={1}", line, prev);
            if (prev < 0) break;

            Assert.AreEqual(line - 1, prev);
            line = prev;
         }
      }
   }

   public class CB : ILogFileCallback
   {
      public Result Result;

      public void OnExportComplete(Result result)
      {
      }

      public void OnLoadComplete(Result result)
      {
         Result = result;
      }

      public void OnLoadCompletePartial(LogFile cloned)
      {
      }

      public void OnProgress(LogFile lf, int percent)
      {
      }

      public void OnSearchComplete(SearchResult result)
      {
      }

      public void OnSearchPartial(LogFile lf, int firstMatch)
      {
      }
   }
}
