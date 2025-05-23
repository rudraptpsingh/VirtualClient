// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Logging
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Abstractions;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Polly;
    using VirtualClient.Common;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;
    using ILogger = Microsoft.Extensions.Logging.ILogger;

    /// <summary>
    /// An <see cref="ILogger"/> implementation for writing metrics data to a CSV file.
    /// </summary>
    public class SummaryFileLogger : ILogger, IFlushableChannel, IDisposable
    {
        /// <summary>
        /// 
        /// </summary>
        public const int MaxLineLength = 150;

        private static readonly Encoding ContentEncoding = Encoding.UTF8;

        private ConcurrentBuffer buffer;
        private string fileDirectory;
        private string filePath;
        private IAsyncPolicy fileAccessRetryPolicy;
        private IFileSystem fileSystem;
        private Task flushTask;
        private bool initialized;
        private SemaphoreSlim semaphore;
        private bool disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SummaryFileLogger"/> class.
        /// </summary>
        /// <param name="filePath">The path to the CSV file to which the metrics should be written.</param>
        /// <param name="retryPolicy"></param>
        public SummaryFileLogger(string filePath, IAsyncPolicy retryPolicy = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                PlatformSpecifics tempPlatformSpecifics = new PlatformSpecifics(Environment.OSVersion.Platform, RuntimeInformation.ProcessArchitecture);
                filePath = tempPlatformSpecifics.Combine(tempPlatformSpecifics.LogsDirectory, "summary.txt");
            }

            this.filePath = filePath;
            this.fileDirectory = Path.GetDirectoryName(filePath);
            this.fileSystem = new FileSystem();
            this.buffer = new ConcurrentBuffer();
            this.fileAccessRetryPolicy = retryPolicy ?? Policy.Handle<IOException>().WaitAndRetryAsync(5, (retries) => TimeSpan.FromSeconds(retries));
            this.semaphore = new SemaphoreSlim(1, 1);
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        /// <summary>
        /// Disposes of resources used by the instance.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Flushes the remaining buffer content to the file system.
        /// </summary>
        /// <param name="timeout">Not used.</param>
        public void Flush(TimeSpan? timeout = null)
        {
            this.FlushBufferAsync().GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            EventContext eventContext = state as EventContext;

            if (eventContext != null)
            {
                try
                {
                    this.semaphore.Wait();
                    if (eventId.Id == (int)LogType.Metric)
                    {
                        string message = SummaryFileLogger.CreateMetricMessage(eventContext);
                        this.buffer.Append(message);
                    }
                    else if (eventId.Id == (int)LogType.Error)
                    {
                        string message = SummaryFileLogger.CreateErrorMessage(eventId, eventContext);
                        this.buffer.Append(message);
                    }
                }
                finally
                {
                    this.semaphore.Release();
                }

                if (this.flushTask == null)
                {
                    this.flushTask = this.MonitorBufferAsync();
                }
            }
        }

        /// <summary>
        /// Disposes of resources used by the instance.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!this.disposed)
                {
                    this.semaphore.Dispose();
                    this.disposed = true;
                }
            }
        }

        private static string CreateMetricMessage(EventContext context)
        {
            StringBuilder messageBuilder = new StringBuilder();
            string scenarioName = context.Properties["scenarioName"].ToString();
            string metricName = context.Properties["metricName"].ToString();
            double metricValue = (double)context.Properties["metricValue"];
            string metricUnit = context.Properties["metricUnit"].ToString();

            messageBuilder.AppendMessage($"| Scenario: {scenarioName} | Name: {metricName} | Value: {metricValue} | Unit: {metricUnit} |" + Environment.NewLine);
            messageBuilder.AppendMessage(Environment.NewLine);
            return messageBuilder.ToString();
        }

        private static string CreateErrorMessage(EventId eventId, EventContext context)
        {
            StringBuilder messageBuilder = new StringBuilder();
            string errorMessage = eventId.Name;
            var errors = context.Properties[EventContextExtensions.ErrorProperty] as List<object>;
            foreach (dynamic error in errors)
            {
                messageBuilder.AppendMessage($"*** Error ***" + Environment.NewLine);
                messageBuilder.AppendMessage($"Error Type: {error.errorType}" + Environment.NewLine);
                messageBuilder.AppendMessage($"Error Message: {error.errorMessage}" + Environment.NewLine);
            }

            messageBuilder.AppendMessage($"Error Call Stack: {context.Properties[EventContextExtensions.ErrorCallstackProperty]}" + Environment.NewLine);
            messageBuilder.AppendMessage(Environment.NewLine);

            return messageBuilder.ToString();
        }

        private Task MonitorBufferAsync()
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (!this.initialized)
                        {
                            this.InitializeFilePaths();
                            this.initialized = true;
                        }

                        await Task.Delay(300);
                        await this.FlushBufferAsync();
                    }
                    catch
                    {
                        // Best effort. We do not want to crash the application on failures to access
                        // the CSV file.
                    }
                }
            });
        }

        private async Task FlushBufferAsync()
        {
            if (this.buffer.Length > 0)
            {
                await this.fileAccessRetryPolicy.ExecuteAsync(async () =>
                {
                    try
                    {
                        await this.semaphore.WaitAsync();

                        using (FileSystemStream fileStream = this.fileSystem.FileStream.New(this.filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            if (fileStream.Length == 0)
                            {
                            }

                            byte[] bufferContents = SummaryFileLogger.ContentEncoding.GetBytes(this.buffer.ToString());

                            fileStream.Position = fileStream.Length;
                            fileStream.Write(bufferContents);
                            await fileStream.FlushAsync();

                            this.buffer.Clear();
                        }
                    }
                    finally
                    {
                        this.semaphore.Release();
                    }
                });
            }
        }

        private void InitializeFilePaths()
        {
            if (!this.fileSystem.Directory.Exists(this.fileDirectory))
            {
                this.fileSystem.Directory.CreateDirectory(this.fileDirectory);
            }
        }

    }

    /// <summary>
    /// 
    /// </summary>
    internal static class SummaryStringBuilderExtensions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="stringBuilder"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        internal static void AppendMessage(this StringBuilder stringBuilder, string message)
        {
            string datetime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string prefix = $"{datetime} | ";
            int prefixLength = prefix.Length;

            // The max length of the actual message per line
            int maxMessageLength = SummaryFileLogger.MaxLineLength - prefixLength;

            int currentIndex = 0;
            string[] lines = message.Split(Environment.NewLine, StringSplitOptions.None);
            foreach (string line in lines)
            {
                while (currentIndex < line.Length)
                {
                    int remaining = line.Length - currentIndex;
                    int lengthToTake = Math.Min(remaining, maxMessageLength);
                    string lineSegment = line.Substring(currentIndex, lengthToTake);

                    if (currentIndex == 0)
                    {
                        stringBuilder.AppendLine(prefix + lineSegment);
                    }
                    else
                    {
                        stringBuilder.AppendLine(new string(' ', prefixLength) + lineSegment);
                    }

                    currentIndex += lengthToTake;
                }
            }
            
        }
    }
}
