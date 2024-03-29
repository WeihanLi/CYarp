﻿using CYarp.Server.Clients;
using CYarp.Server.Configs;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CYarp.Server.Middlewares
{
    sealed partial class CYarpConnection : IConnection
    {
        private readonly string id;
        private readonly Stream stream;
        private readonly ILogger logger;
        private readonly Timer? keepAliveTimer;
        private readonly TimeSpan keepAliveTimeout;
        private readonly CancellationTokenSource disposeTokenSource = new();

        private static readonly int bufferSize = 8;
        private static readonly string Ping = "PING";
        private static readonly ReadOnlyMemory<byte> PingLine = "PING\r\n"u8.ToArray();
        private static readonly ReadOnlyMemory<byte> PongLine = "PONG\r\n"u8.ToArray();

        public string Id => this.id;

        public CYarpConnection(string id, Stream stream, ConnectionConfig config, ILogger logger)
        {
            this.id = id;
            this.stream = stream;
            this.logger = logger;

            var keepAliveInterval = config.KeepAliveInterval;
            if (config.KeepAlive && keepAliveInterval > TimeSpan.Zero)
            {
                this.keepAliveTimeout = keepAliveInterval.Add(TimeSpan.FromSeconds(10d));
                this.keepAliveTimer = new Timer(this.KeepAliveTimerTick, null, keepAliveInterval, keepAliveInterval);
            }
            else
            {
                this.keepAliveTimeout = Timeout.InfiniteTimeSpan;
            }
        }

        /// <summary>
        /// 心跳timer
        /// </summary>
        /// <param name="state"></param>
        private async void KeepAliveTimerTick(object? state)
        {
            try
            {
                await this.stream.WriteAsync(PingLine);
                Log.LogPing(this.logger, this.id);
            }
            catch (Exception)
            {
                this.keepAliveTimer?.Dispose();
            }
        }

        public async Task CreateHttpTunnelAsync(Guid tunnelId, CancellationToken cancellationToken)
        {
            const int size = 64;
            var tunnelIdLine = $"{tunnelId}\r\n";

            using var owner = MemoryPool<byte>.Shared.Rent(size);
            var length = Encoding.ASCII.GetBytes(tunnelIdLine, owner.Memory.Span);

            var buffer = owner.Memory[..length];
            await this.stream.WriteAsync(buffer, cancellationToken);
        }

        public async Task WaitForCloseAsync()
        {
            try
            {
                var cancellationToken = this.disposeTokenSource.Token;
                await this.HandleConnectionAsync(cancellationToken);
            }
            catch (Exception)
            {
            }
        }

        private async Task HandleConnectionAsync(CancellationToken cancellationToken)
        {
            using var textReader = new StreamReader(this.stream, bufferSize: bufferSize, leaveOpen: true);
            while (cancellationToken.IsCancellationRequested == false)
            {
                var textTask = textReader.ReadLineAsync(cancellationToken);
                var text = this.keepAliveTimeout <= TimeSpan.Zero
                    ? await textTask
                    : await textTask.AsTask().WaitAsync(this.keepAliveTimeout, cancellationToken);

                if (text == null)
                {
                    break;
                }
                else if (text == Ping)
                {
                    Log.LogPong(this.logger, this.id);
                    await this.stream.WriteAsync(PongLine, cancellationToken);
                }
            }
        }

        public void Dispose()
        {
            if (this.disposeTokenSource.IsCancellationRequested == false)
            {
                this.disposeTokenSource.Cancel();
                this.disposeTokenSource.Dispose();
            }
            GC.SuppressFinalize(this);
        }


        static partial class Log
        {
            [LoggerMessage(LogLevel.Debug, "[{clienId}] 发出PING心跳")]
            public static partial void LogPing(ILogger logger, string clienId);

            [LoggerMessage(LogLevel.Debug, "[{clienId}] 回复PONG心跳")]
            public static partial void LogPong(ILogger logger, string clienId);

            [LoggerMessage(LogLevel.Debug, "[{clienId}] 连接已关闭")]
            public static partial void LogClosed(ILogger logger, string clienId);
        }
    }
}
