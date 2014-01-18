﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Medidata.Lumberjack.Core.Data;
using Medidata.Lumberjack.Core.Data.Fields;
using Medidata.Lumberjack.Core.Data.Fields.Values;
using Medidata.Lumberjack.Core.Data.Formats;

namespace Medidata.Lumberjack.Core.Processing
{
    /// <summary>
    /// 
    /// </summary>
    public sealed class EntryParsingEngine : EngineBase
    {
        #region Constants

        private const FormatContextEnum ContextType = FormatContextEnum.Entry;
        private const int BufferSize = 1024 * 8;

        #endregion
        
        #region Initializers
        
        /// <summary>
        /// 
        /// </summary>
        public EntryParsingEngine() : this(null) { }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="session"></param>
        public EntryParsingEngine(UserSession session) : base(session) { }

        #endregion
        
        #region Base overrides

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logFile"></param>
        /// <returns></returns>
        public override bool TestIfProcessable(LogFile logFile) {
            return logFile.EntryParseStatus == EngineStatusEnum.None && logFile.SessionFormat != null;
        }

        /// <summary>
        /// 
        /// </summary>
        protected override void ProcessStart() {
            
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logFile"></param>
        /// <param name="engineMetrics"></param>
        /// <returns></returns>
        protected override bool ProcessLog(LogFile logFile, ref EngineMetrics engineMetrics) {
            var success = false;

            logFile.EntryParseStatus = EngineStatusEnum.Processing;

            if (logFile.Filesize == 0) {
                // Log file is 0 bytes, consider this a success and go on with our life
                return true;
            }

            var state = new EngineState();
            var regex = logFile.SessionFormat.Contexts[ContextType].Regex;

            try {
                using (var fs = new FileStream(logFile.FullFilename, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                using (var sr = new StreamReader(fs)) {
                    state.StreamReader = sr;

                    var buffer = new Char[BufferSize];
                    var entries = new List<Entry>(1024);
                    var values = new List<FieldValue>(4096);
                    var remaining = "";

                    OnProgressChanged(engineMetrics, logFile);

                    while (!IsStopping) {
                        var count = sr.Read(buffer, 0, (int) (Math.Min(BufferSize, state.TotalBytes - state.BytesRead)));
                        if (count == 0)
                            break;

                        var text = new string(buffer, 0, count);
                        var view = String.Concat(remaining, text);
                        var lastPos = 0;
                        var sw = new Stopwatch();
                        sw.Start();
                        var matches = regex.Matches(view);

                        for (var i = 0; i < matches.Count && !IsStopping; i++) {
                            var match = matches[i];

                            if (!match.Success)
                                continue;

                            var position = state.BytesProcessed + state.Encoding.GetByteCount(view.Substring(0, match.Index));
                            var entry = new Entry(logFile, position, (ushort) match.Length);// {Id = EntryCollection.GetNextId()};

                            lastPos = match.Index + match.Length - remaining.Length;

                            // TODO: Special case needs to be implemented for TIMESTAMP field

                            var fieldValues = FieldValueFactory.MatchFieldValues(entry, ContextType, match, FieldValuePredicate);
                            if (fieldValues != null) {
                                values.AddRange(fieldValues);
                                logFile.EntryStats.TotalEntries++;
                                entries.Add(entry);
                            } else {
                                OnInfo(String.Format("Failed to parse log entry at byte position {0:##,#} in file \"{1}\".", position, logFile.Filename));
                                break;
                            }
                        }

                        var bytes = state.Encoding.GetByteCount(view) - (count - lastPos);
                        state.BytesProcessed += bytes;
                        state.BytesRead += count;

                            remaining = (lastPos < count) ? text.Substring(lastPos) : "";
                        

                        engineMetrics.ProcessedBytes += state.Encoding.GetByteCount(text);
                        AddToMetrics(entries.ToArray(), ref engineMetrics);

                        var metrics = engineMetrics;
                        
                        //Task.Factory.StartNew(() => {
                               SessionInstance.FieldValues.Add(values.ToArray());
                               SessionInstance.Entries.Add(entries.ToArray());
                               OnProgressChanged(metrics, logFile);
                        //   });

                        entries.Clear();
                        values.Clear();

                        sw.Stop();
                        Debug.WriteLine(sw.ElapsedMilliseconds.ToString());

                        //len = log.Entries.Length;
                        //log.EntryStats.LastEntry = len > 0 ? log.Entries[len - 1].Timestamp : DateTime.MinValue;
                        //log.EntryStats.FirstEntry = len > 0 ? log.Entries[0].Timestamp : DateTime.MinValue;
                    }

                    sr.Close();
                }

                success = true;
            } catch (Exception ex) {
                OnError(String.Format("Failed to parse log entries for \"{0}\".", logFile.Filename), ex);
            }

            if (success) {
                engineMetrics.ProcessedLogs++;
            } else {
                engineMetrics.TotalLogs--;
                engineMetrics.TotalBytes -= logFile.Filesize;
            }

            return success;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logFile"></param>
        /// <param name="success"></param>
        /// <param name="timeElapsed"></param>
        protected override void ProcessComplete(LogFile logFile, bool success, long timeElapsed) {
            logFile.ProcessTimeElapse[ProcessTypeEnum.Entries] = timeElapsed;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logFile"></param>
        /// <param name="status"></param>
        protected override void SetLogProcessStatus(LogFile logFile, EngineStatusEnum status) {
            logFile.EntryParseStatus = status;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logFile"></param>
        /// <returns></returns>
        protected override EngineStatusEnum GetLogProcessStatus(LogFile logFile) {
            return logFile.EntryParseStatus;
        }

        #endregion

        #region Private methods

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldValue"></param>
        /// <returns></returns>
        private static bool FieldValuePredicate(FieldValue fieldValue) {
            var logFile = fieldValue.LogFile;
            var formatField = fieldValue.FormatField;

            if (!formatField.Filterable && formatField.DataType == FieldDataTypeEnum.String)
                return false;

            if (formatField.Name.Equals("LEVEL")) {
                switch (fieldValue.ToString().ToUpper()) {
                    case "TRACE":
                        logFile.EntryStats.Trace++;
                        break;
                    case "DEBUG":
                        logFile.EntryStats.Debug++;
                        break;
                    case "INFO":
                        logFile.EntryStats.Info++;
                        break;
                    case "WARN":
                        logFile.EntryStats.Warn++;
                        break;
                    case "ERROR":
                        logFile.EntryStats.Error++;
                        break;
                    case "FATAL":
                        logFile.EntryStats.Fatal++;
                        break;
                }
            }

            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="entries"></param>
        /// <param name="metrics"></param>
        private static void AddToMetrics(Entry[] entries, ref EngineMetrics metrics) {
            ushort size = 0;
            var len = entries.Length;

            for (var i = 0; i < len; i++)
                size += entries[i].Length;

            if (metrics.ProcessedEntries > 0) {
                metrics.AvgEntrySize = (ushort) ((metrics.AvgEntrySize + size)/(len + 1));
            } else {
                metrics.AvgEntrySize = (ushort)( len == 0 ? 0 : (size / len));
            }

            metrics.ProcessedEntries += len;
        }

        #endregion
    }
}
