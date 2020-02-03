namespace Omnius.Core.Network.Connections.Multiplexer.V1
{
    /*
    internal sealed partial class MultiplexService
    {
        private readonly Communicator _communicator;
        private readonly SessionIdGenerateType _sessionIdGenerateType;
        private readonly BufferPool _bytesPool;

        private readonly Dictionary<ulong, SendSessionCreateRequestInfo> _sendSessionCreateRequestInfoMap;
        private readonly object _sendSessionCreateRequestLockObject = new object();

        private readonly Channel<ReceiveSessionCreateRequestInfo> _receiveSessionCreateRequestInfoChannel;

        private readonly SessionIdGenerator _sessionIdManager;

        private readonly Random _random = new Random();
        private readonly AsyncLock _connectLock = new AsyncLock();

        public MultiplexService(IConnection connection, SessionIdGenerateType sessionIdGenerateType, BufferPool bytesPool)
        {
            _communicator = new Communicator(connection);
            _sessionIdGenerateType = sessionIdGenerateType;
            _bytesPool = bytesPool;

            _sendSessionCreateRequestInfoMap = new Dictionary<ulong, SendSessionCreateRequestInfo>();
            _receiveSessionCreateRequestInfoChannel = Channel.CreateBounded<ReceiveSessionCreateRequestInfo>(new BoundedChannelOptions(30) { FullMode = BoundedChannelFullMode.DropOldest, });
            _sessionIdManager = new SessionIdGenerator(sessionIdGenerateType);

            this.Init();
        }

        private void Init()
        {
            _communicator.ReceiveSessionConnectMessageEvent += (message) =>
            {
                var info = new ReceiveSessionCreateRequestInfo();
                info.SessionId = message.SessionId;
                info.CreationTime = DateTime.UtcNow;

                _receiveSessionCreateRequestInfoChannel.Writer.TryWrite(info);
            };

            _communicator.ReceiveSessionAcceptMessageEvent += (message) =>
            {
                lock (_sendSessionCreateRequestLockObject)
                {
                    if (_sendSessionCreateRequestInfoMap.TryGetValue(message.SessionId, out var info))
                    {
                        info.TaskCompletionSource.SetResult(true);
                        _sendSessionCreateRequestInfoMap.Remove(message.SessionId);
                    }
                }
            };

            //  public event Action<SessionAcceptMessage> ReceiveSessionAcceptMessageEvent;
            //  public event Action<SessionUpdateWindowSizeMessage> ReceiveSessionUpdateWindowSizeMessageEvent;
            //  public event Action<SessionDataMessage> ReceiveSessionDataMessageEvent;
            //  public event Action<SessionCloseMessage> ReceiveSessionCloseMessageEvent;
            //  public event Action<SessionErrorMessage> ReceiveSessionErrorMessageEvent;
        }

        public async ValueTask<ulong> ConnectAsync(CancellationToken cancellationToken = default)
        {
            // StreamのIDを発行する
            var id = _sessionIdManager.Create();

            try
            {
                var info = new SendSessionCreateRequestInfo();

                lock (_sendSessionCreateRequestLockObject)
                {
                    _sendSessionCreateRequestInfoMap.Add(id, info);
                }

                // SessionConnectメッセージを送信する
                await _communicator.SendSessionConnectMessageAsync(new SessionConnectMessage(id), cancellationToken);

                // SessionAcceptメッセージを待機する
                var result = await info.TaskCompletionSource.WaitAsync(cancellationToken);

                // 接続失敗
                if (!result)
                {
                    throw new Exception();
                }

                return id;
            }
            catch (OperationCanceledException)
            {
                lock (_sendSessionCreateRequestLockObject)
                {
                    _sendSessionCreateRequestInfoMap.Remove(id);
                }

                // SessionCloseメッセージを送信する
                await _communicator.SendSessionCloseMessageAsync(new SessionCloseMessage(id), cancellationToken);

                throw;
            }
        }

        public async ValueTask<ulong> AcceptAsync(CancellationToken cancellationToken = default)
        {
            for (; ; )
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = await _receiveSessionCreateRequestInfoChannel.Reader.ReadAsync(cancellationToken);
                await _communicator.SendSessionAcceptMessageAsync(new SessionAcceptMessage(info.SessionId));

                return info.SessionId;
            }
        }

        private sealed class SendSessionCreateRequestInfo
        {
            public TaskCompletionSource<bool> TaskCompletionSource { get; } = new TaskCompletionSource<bool>();
        }

        private sealed class ReceiveSessionCreateRequestInfo
        {
            public ulong SessionId { get; set; }
            public DateTime CreationTime { get; set; }
        }
    }*/
}
