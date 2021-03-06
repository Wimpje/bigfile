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

using Bitmanager.Core;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Bitmanager.BigFile
{
   /// <summary>
   /// 
   /// </summary>
   public partial class FormSettings : Form
   {
      private readonly Settings settings;

      public FormSettings(Settings settings)
      {
         InitializeComponent();
         this.settings = settings;
         comboNumLines.SelectedIndex = this.settings.NumContextLines;
         txtHilight.Text = ColorTranslator.ToHtml(settings.HighlightColor);
         txtContext.Text = ColorTranslator.ToHtml(settings.ContextColor);
         txtGZip.Text = settings.GzipExe;
         cbSearchThreads.Text = settings.SearchThreadsAsText;
         cbCompress.Text = settings.CompressMemoryIfBigger;
         cbInMemory.Text = settings.LoadMemoryIfBigger;
         label6.Text = Invariant.Format("(#cpu's: {0})", Environment.ProcessorCount);
      }

      private void buttonOK_Click(object sender, EventArgs e)
      {
         settings.HighlightColor = ColorTranslator.FromHtml(txtHilight.Text);
         settings.ContextColor = ColorTranslator.FromHtml(txtContext.Text);
         settings.NumContextLines = comboNumLines.SelectedIndex;
         settings.GzipExe = txtGZip.Text;
         settings.SearchThreadsAsText = cbSearchThreads.Text;
         settings.CompressMemoryIfBigger = Settings.CheckAndRepairSize(cbCompress.Text, true);
         settings.LoadMemoryIfBigger = cbInMemory.Text;
         settings.Save();

         this.DialogResult = DialogResult.OK;
      }

      private void buttonCancel_Click(object sender, EventArgs e)
      {
         this.DialogResult = DialogResult.Cancel;
      }

      private void colorBox_Click(object sender, EventArgs e)
      {
         var t = (TextBox)sender;
         colorDialog1.Color = ColorTranslator.FromHtml(t.Text);
         if (colorDialog1.ShowDialog() != DialogResult.OK) return;
         t.Text = ColorTranslator.ToHtml(colorDialog1.Color);
         t.Tag = colorDialog1.Color;
      }


      private void FormSettings_Load(object sender, EventArgs e)
      {
         label9.Text = "";
         if (!Globals.CanCompress)
         {
            label9.Text = "No or old bmucore102_64.dll";
            cbCompress.Enabled = false;
         }
      }
   }
}
