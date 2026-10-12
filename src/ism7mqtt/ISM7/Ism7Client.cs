using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using ism7mqtt.ISM7;
using ism7mqtt.ISM7.Protocol;
using ism7ssl;

namespace ism7mqtt
{
    public class Ism7Client
    {
        private readonly Func<Ism7Config, CancellationToken, Task> _messageHandler;
        private readonly string _host;
        private readonly ConcurrentDictionary<Type, XmlSerializer> _serializers = new ConcurrentDictionary<Type, XmlSerializer>();
        private readonly Ism7Config _config;
        private readonly Pipe _pipe;
        private readonly ResponseDispatcher _dispatcher = new ResponseDispatcher();
        private short _lastKeepAlive = 0;
        private Stream _sslStream;

        public int Interval { get; set; }

        public int StartupTimeout { get; set; } = 90;

        private TimeSpan StartupTimeoutSpan => TimeSpan.FromSeconds(StartupTimeout);

        public bool EnableDebug { get; set; }

        public Func<Ism7Config, CancellationToken, Task> OnInitializationFinishedAsync { get; set; }

        /// <summary>
        /// Time programmes cannot be subscribed to like info numbers, so they are read at start and again at this
        /// interval. They only change when someone edits them.
        /// </summary>
        public TimeSpan TimeprogRefresh { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>How long to wait for a time programme bundle before giving it up (it never stops the client).</summary>
        public TimeSpan TimeprogTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public Ism7Client(Func<Ism7Config, CancellationToken, Task> messageHandler, string parameterPath, string host, Ism7Localizer localizer, string parameterXmlOverridePath = null)
        {
            _messageHandler = messageHandler;
            _host = host;
            _config = new Ism7Config(parameterPath, localizer, parameterXmlOverridePath);
            _pipe = new Pipe();
        }

        public async Task RunAsync(string password, CancellationToken cancellationToken)
        {
            using (_sslStream = await ConnectAsync(cancellationToken))
            {
                var fillPipeTask = FillPipeAsync(_pipe.Writer, cancellationToken);
                var readPipeTask = ReadPipeAsync(_pipe.Reader, cancellationToken);
                await AuthenticateAsync(password, cancellationToken);
                var keepAlive = KeepAliveAsync(cancellationToken);
                await Task.WhenAny(fillPipeTask, readPipeTask, keepAlive);
            }
        }

        private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(_host, _config.TcpPort, cancellationToken);
            var ssl = new Ism7SslStream(tcp.Client);
            await ssl.AuthenticateAsClientAsync(cancellationToken);
            return ssl;
        }

        public Task OnCommandAsync(string mqttTopic, JsonObject data, CancellationToken cancellationToken)
        {
            try
            {
                var writeRequests = _config.GetWriteRequest(mqttTopic, data).ToList();
                var timeprogWrites = _config.GetTimeprogWriteRequest(mqttTopic, data).ToList();
                if (writeRequests.Count == 0 && timeprogWrites.Count == 0)
                {
                    Console.WriteLine($"nothing to send for topic '{mqttTopic}' with payload '{data.ToJsonString()}'");
                    return Task.CompletedTask;
                }
                return OnCommandAsync(writeRequests, timeprogWrites, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error for topic '{mqttTopic}' with payload '{data.ToJsonString()}'\n{ex.Message}");
                return Task.CompletedTask;
            }
        }

        public Task OnCommandAsync(string mqttTopic, ReadOnlyMemory<string> propertyParts, string value, CancellationToken cancellationToken)
        {
            try
            {
                var writeRequests = _config.GetWriteRequest(mqttTopic, propertyParts, value).ToList();
                var timeprogWrites = _config.GetTimeprogWriteRequest(mqttTopic, propertyParts, value).ToList();
                if (writeRequests.Count == 0 && timeprogWrites.Count == 0)
                {
                    Console.WriteLine($"nothing to send for topic '{mqttTopic}' with payload '{value}'");
                    return Task.CompletedTask;
                }
                return OnCommandAsync(writeRequests, timeprogWrites, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error for topic '{mqttTopic}' with payload '{value}'\n{ex.Message}");
                return Task.CompletedTask;
            }
        }

        private async Task OnCommandAsync(List<InfoWrite> writeRequests, List<TimeprogWrite> timeprogWrites, CancellationToken cancellationToken)
        {
            if (writeRequests.Count > 0)
            {
                await OnCommandAsync(writeRequests, cancellationToken);
            }
            // Time programme writes go in write bundles of their own, at most seven entries each, as reads do.
            for (int i = 0; i < timeprogWrites.Count; i += Ism7Config.TimeprogBundleSize)
            {
                var writes = timeprogWrites.Skip(i).Take(Ism7Config.TimeprogBundleSize).ToList();
                foreach (var write in writes)
                {
                    write.Seq = $"D;{IdGenerator.GetNextSequenceId()}";
                }
                var request = new TelegramBundleReq
                {
                    AbortOnError = true,
                    BundleId = NextBundleId(),
                    GatewayId = "1",
                    TelegramBundleType = TelegramBundleType.write,
                    TimeprogWriteTelegrams = writes
                };
                _dispatcher.SubscribeOnce(x => x.MessageType == PayloadType.TgrBundleResp && ((TelegramBundleResp)x).BundleId == request.BundleId, OnTimeprogWriteResponse);
                await SendAsync(request, cancellationToken);
            }
        }

        private async Task OnTimeprogWriteResponse(IResponse response, CancellationToken cancellationToken)
        {
            var resp = (TelegramBundleResp)response;
            if (!String.IsNullOrEmpty(resp.Errormsg) || resp.State != TelegrResponseState.OK)
            {
                await Console.Error.WriteLineAsync($"time programme write {resp.BundleId}: state '{resp.State}' {resp.Errormsg}");
                return;
            }
            if (resp.TimeprogWriteTelegrams is null) return;
            foreach (var failed in resp.TimeprogWriteTelegrams.Where(x => x.State != TelegrResponseState.OK))
            {
                await Console.Error.WriteLineAsync($"time programme write refused: type {failed.ProgramType}, programme {failed.HeatprogNumber}, day {failed.DayOfWeek}, state '{failed.State}'");
            }
            var (changed, confirm) = _config.ProcessData(resp.TimeprogWriteTelegrams.Where(x => x.State == TelegrResponseState.OK));
            if (changed)
            {
                await _messageHandler(_config, cancellationToken);
            }
            // Read the days back, so what is published is what the BM-2 stored, not what was sent. Not awaited:
            // this runs on the response path, which must not wait for another response.
            foreach (var group in confirm.GroupBy(x => x.busAddress))
            {
                var reads = group.Select(x => x.read).ToList();
                _ = PullTimeprogBundlesAsync(group.Key, reads, cancellationToken);
            }
        }

        private async Task OnCommandAsync(List<InfoWrite> writeRequests, CancellationToken cancellationToken)
        {
            var request = new TelegramBundleReq
            {
                AbortOnError = true,
                BundleId = NextBundleId(),
                GatewayId = "1",
                TelegramBundleType = TelegramBundleType.write,
                InfoWriteTelegrams = writeRequests
            };
            _dispatcher.SubscribeOnce(x=>x.MessageType == PayloadType.TgrBundleResp && ((TelegramBundleResp)x).BundleId == request.BundleId, OnWriteResponse);
            await SendAsync(request, cancellationToken);
        }

        private async Task OnWriteResponse(IResponse response, CancellationToken cancellationToken)
        {
            var resp = (TelegramBundleResp) response;
            if (!String.IsNullOrEmpty(resp.Errormsg))
                throw new InvalidDataException(resp.Errormsg);
            if (resp.State != TelegrResponseState.OK)
                throw new InvalidDataException($"unexpected state '{resp.State}");
            
            
            var hasDatapoints = _config.ProcessData(resp.WriteTelegrams.Where(x => x.State == TelegrResponseState.OK));
            if (hasDatapoints)
            {
                await _messageHandler(_config, cancellationToken);
            }
        }

        private async Task FillPipeAsync(PipeWriter target, CancellationToken cancellationToken)
        {
            const int bufferSize = 512;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var buffer = target.GetMemory(bufferSize);
                    var read = await _sslStream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    target.Advance(read);
                    var result = await target.FlushAsync(cancellationToken);
                    if (result.IsCanceled || result.IsCompleted)
                        break;
                }
                await target.CompleteAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                await target.CompleteAsync(ex);
            }
        }

        private async Task ReadPipeAsync(PipeReader source, CancellationToken cancellationToken)
        {
            try
            {
                var header = new byte[6];
                var tasks = new List<Task>();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var readValueTask = source.ReadAsync(cancellationToken);
                    ReadResult result;
                    if (readValueTask.IsCompleted)
                    {
                        result = await readValueTask;
                    }
                    else
                    {
                        var readTask = readValueTask.AsTask();
                        tasks.Add(readTask);
                        while (true)
                        {
                            var done = await Task.WhenAny(tasks);
                            tasks.Remove(done);                          
                            if (done.IsFaulted)
                                await done;
                            if (done == readTask) break;
                        }

                        result = await readTask;
                    }

                    var buffer = result.Buffer;
                    while (buffer.Length >= 6)
                    {
                        var size = buffer.Slice(0, 6);
                        size.CopyTo(header);
                        var length = BinaryPrimitives.ReadInt32BigEndian(header);
                        if (buffer.Length < length + 6) break;
                        var type = (PayloadType)BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(4));
                        var xmlBuffer = buffer.Slice(6, length);
                        if (EnableDebug)
                        {
                            var xml = Encoding.UTF8.GetString(xmlBuffer);
                            Console.WriteLine($"< {xml}");
                        }
                        if (xmlBuffer.Length == 0)
                        {
                            Console.WriteLine($"Received empty xml of type '{type}' - skipping dispatch (raw '{HexEncode(header)}')");
                        }
                        else
                        {
                            var response = Deserialize(type, new ReadOnlySequenceStream(xmlBuffer));
                            tasks.Add(_dispatcher.DispatchAsync(response, cancellationToken));
                        }
                        buffer = buffer.Slice(xmlBuffer.End);
                        
                    }
                    source.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
                await source.CompleteAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                await source.CompleteAsync(ex);
            }
        }

        private static string HexEncode(ReadOnlySpan<byte> data)
        {
            StringBuilder s = new StringBuilder(data.Length*2);
            foreach (byte b in data)
                s.Append(b.ToString("x2"));
            return s.ToString();
        }

        private async Task SubscribeAsync(string busAddress, string bundleId, CancellationToken cancellationToken)
        {
            var infoReads = _config.GetBundle(bundleId);
            bundleId = NextBundleId();
            _dispatcher.Subscribe(x => x.MessageType == PayloadType.TgrBundleResp && ((TelegramBundleResp) x).BundleId == bundleId, OnPushResponseAsync);
            foreach (var infoRead in infoReads)
            {
                infoRead.BusAddress = busAddress;
                infoRead.Intervall = Interval;
                infoRead.Seq = NextSequenceId();
            }
            await SendAsync(new TelegramBundleReq
            {
                AbortOnError = false,
                BundleId = bundleId,
                GatewayId = "1",
                TelegramBundleType = TelegramBundleType.push,
                InfoReadTelegrams = infoReads
            }, cancellationToken);
        }

        private async Task OnPushResponseAsync(IResponse response, CancellationToken cancellationToken)
        {
            var resp = (TelegramBundleResp) response;
            if (!String.IsNullOrEmpty(resp.Errormsg))
                throw new InvalidDataException(resp.Errormsg);
            if (resp.State != TelegrResponseState.OK)
            {
                await Console.Error.WriteLineAsync($"unexpected state '{resp.State}");
            }
            else
            {
                var hasDatapoints = _config.ProcessData(resp.Telegrams.Where(x => x.State == TelegrResponseState.OK));
                if (hasDatapoints)
                {
                    await _messageHandler(_config, cancellationToken);
                }
            }
        }

        private async Task LoadInitialValuesAsync(CancellationToken cancellationToken)
        {
            var semaphore = new SemaphoreSlim(1, 1);
            var pendingSubscriptions = new List<(string busAddress, string bundleId)>();

            // Phase 1: pull all bundles, one at a time as FW5.1+ has problems with too many pull requests in parallel
            var busAddresses = new List<string>();
            foreach (var busAddress in _config.AddAllDevices(_host))
            {
                busAddresses.Add(busAddress);
                var bundles = _config.GetBundlesForDevice(busAddress);
                foreach (var (bundleId, infoReads) in bundles)
                {
                    _dispatcher.SubscribeOnce(
                        x => x.MessageType == PayloadType.TgrBundleResp && ((TelegramBundleResp)x).BundleId == bundleId,
                        async (r, c) =>
                        {
                            try
                            {
                                await OnInitialValuesAsync(r, pendingSubscriptions, c);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }
                    );

                    foreach (var infoRead in infoReads)
                    {
                        infoRead.BusAddress = busAddress;
                        infoRead.Seq = NextSequenceId();
                    }

                    await WaitForBundleResponseAsync(semaphore, StartupTimeoutSpan, cancellationToken);
                    await SendAsync(new TelegramBundleReq
                    {
                        AbortOnError = false,
                        BundleId = bundleId,
                        GatewayId = "1",
                        TelegramBundleType = TelegramBundleType.pull,
                        InfoReadTelegrams = infoReads
                    }, cancellationToken);
                }
            }

            // Wait for the last pull response to be fully processed before subscribing.
            await WaitForBundleResponseAsync(semaphore, StartupTimeoutSpan, cancellationToken);

            await PullTimeprogsAsync(busAddresses, cancellationToken);

            // Phase 2: send push subscribes after all pulls are complete
            foreach (var (busAddress, bundleId) in pendingSubscriptions)
            {
                await SubscribeAsync(busAddress, bundleId, cancellationToken);
            }

            if (OnInitializationFinishedAsync is not null)
            {
                await OnInitializationFinishedAsync(_config, cancellationToken);
            }

            _ = RefreshTimeprogsAsync(busAddresses, cancellationToken);
        }

        private async Task RefreshTimeprogsAsync(List<string> busAddresses, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeprogRefresh, cancellationToken);
                    await PullTimeprogsAsync(busAddresses, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"time programme refresh stopped: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads every time programme day of the given devices, one bundle at a time. A bundle the ISM7 does not
        /// answer in time is skipped, never fatal: the info number values do not depend on it.
        /// </summary>
        private async Task PullTimeprogsAsync(List<string> busAddresses, CancellationToken cancellationToken)
        {
            foreach (var busAddress in busAddresses)
            {
                foreach (var reads in _config.GetTimeprogBundlesForDevice(busAddress))
                {
                    await PullTimeprogBundleAsync(busAddress, reads, cancellationToken);
                }
            }
        }

        private async Task PullTimeprogBundlesAsync(string busAddress, List<TimeprogRead> reads, CancellationToken cancellationToken)
        {
            try
            {
                for (int i = 0; i < reads.Count; i += Ism7Config.TimeprogBundleSize)
                {
                    await PullTimeprogBundleAsync(busAddress, reads.Skip(i).Take(Ism7Config.TimeprogBundleSize).ToList(), cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync($"time programme read-back failed: {ex.Message}");
            }
        }

        private async Task PullTimeprogBundleAsync(string busAddress, List<TimeprogRead> reads, CancellationToken cancellationToken)
        {
            var bundleId = NextBundleId();
            var done = new SemaphoreSlim(0, 1);
            _dispatcher.SubscribeOnce(
                x => x.MessageType == PayloadType.TgrBundleResp && ((TelegramBundleResp)x).BundleId == bundleId,
                async (r, c) =>
                {
                    try
                    {
                        await OnTimeprogValuesAsync(r, c);
                    }
                    finally
                    {
                        done.Release();
                    }
                });
            foreach (var read in reads)
            {
                read.BusAddress = busAddress;
                read.Seq = $"D;{IdGenerator.GetNextSequenceId()}";
            }
            await SendAsync(new TelegramBundleReq
            {
                AbortOnError = false,
                BundleId = bundleId,
                GatewayId = "1",
                TelegramBundleType = TelegramBundleType.pull,
                TimeprogReadTelegrams = reads
            }, cancellationToken);
            if (!await done.WaitAsync(TimeprogTimeout, cancellationToken))
            {
                await Console.Error.WriteLineAsync($"no answer to time programme bundle {bundleId} for {busAddress}");
            }
        }

        private async Task OnTimeprogValuesAsync(IResponse response, CancellationToken cancellationToken)
        {
            var resp = (TelegramBundleResp)response;
            if (!String.IsNullOrEmpty(resp.Errormsg) || resp.State != TelegrResponseState.OK)
            {
                await Console.Error.WriteLineAsync($"time programme bundle {resp.BundleId}: state '{resp.State}' {resp.Errormsg}");
                return;
            }
            if (resp.TimeprogTelegrams is null) return;
            if (_config.ProcessData(resp.TimeprogTelegrams.Where(x => x.State == TelegrResponseState.OK)))
            {
                await _messageHandler(_config, cancellationToken);
            }
        }

        // We observed pull bundel request were not answered by the ISM7 which caused the startup to hang.
        // With a timeout for each pull request we make sure the startup is interrupted and the user can see the problem in the logs.
        private static async Task WaitForBundleResponseAsync(SemaphoreSlim semaphore, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await semaphore.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Shutdown ism7mqtt due to timeout during startup.");
            }
        }

        private async Task OnInitialValuesAsync(IResponse response, List<(string busAddress, string bundleId)> pendingSubscriptions, CancellationToken cancellationToken)
        {
            var resp = (TelegramBundleResp) response;
            if (!String.IsNullOrEmpty(resp.Errormsg))
                throw new InvalidDataException(resp.Errormsg);
            if (resp.State != TelegrResponseState.OK)
                throw new InvalidDataException($"unexpected state '{resp.State}'");
            if (resp.Telegrams.Any())
            {
                var hasDatapoints = _config.ProcessData(resp.Telegrams.Where(x => x.State == TelegrResponseState.OK));
                if (hasDatapoints)
                {
                    await _messageHandler(_config, cancellationToken);
                    var busAddress = resp.Telegrams.Select(x => x.BusAddress).First();
                    pendingSubscriptions.Add((busAddress, resp.BundleId));
                }
            }
        }

        private ValueTask AuthenticateAsync(string password, CancellationToken cancellationToken)
        {
            _dispatcher.SubscribeOnce(x => x.MessageType == PayloadType.DirectLogonResp,
                OnAuthenticateAsync);
            return SendAsync(new LoginReq {Password = password}, cancellationToken);
        }

        private Task OnAuthenticateAsync(IResponse response, CancellationToken cancellationToken)
        {
            var resp = (LoginResp)response;
            if (resp.State != LoginState.ok)
                throw new InvalidDataException("invalid login state");
            return LoadInitialValuesAsync(cancellationToken);
        }

        private async Task KeepAliveAsync(CancellationToken cancellationToken)
        {
            _dispatcher.Subscribe(x=>x.MessageType == PayloadType.KeepAlive, OnKeepAliveAsync);
            short currentKeepAlive = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                if (currentKeepAlive != _lastKeepAlive)
                    throw new TimeoutException("No keepalive response within the last 60 seconds");
                currentKeepAlive++;
                await SendAsync(new KeepAliveReq(currentKeepAlive), cancellationToken);
            }
        }

        private Task OnKeepAliveAsync(IResponse response, CancellationToken cancellationToken)
        {
            var resp = (KeepAliveResp) response;
            _lastKeepAlive = resp.Seq;
            return Task.CompletedTask;
        }

        private ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken) where T:IPayload
        {
            var data = payload.Serialize();
            if (EnableDebug)
                Console.WriteLine($"> {Encoding.UTF8.GetString(data)}");
            var buffer = new byte[data.Length + 6];
            BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
            BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(4), (short) payload.Type);
            Buffer.BlockCopy(data, 0, buffer, 6, data.Length);
            return _sslStream.WriteAsync(buffer, cancellationToken);
        }

        private static string NextBundleId()
        {
            return IdGenerator.GetNextBundleIdString();
        }

        private static string NextSequenceId()
        {
            var id = IdGenerator.GetNextSequenceId();
            return $"A;{id}";
        }

        private IResponse Deserialize(PayloadType type, Stream data)
        {
            switch (type)
            {
                case PayloadType.DirectLogonResp:
                    return (IResponse) GetSerializer<LoginResp>().Deserialize(data);
                case PayloadType.SystemconfigResp:
                    return (IResponse) GetSerializer<SystemconfigResp>().Deserialize(data);
                case PayloadType.TgrBundleResp:
                    return (IResponse) GetSerializer<TelegramBundleResp>().Deserialize(data);
                case PayloadType.KeepAlive:
                    using (var reader = new BinaryReader(data))
                    {
                        var buffer = reader.ReadBytes(2);
                        return new KeepAliveResp(BinaryPrimitives.ReadInt16BigEndian(buffer));
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, "unsupported payload type");
            }
        }

        private XmlSerializer GetSerializer<T>()
        {
            return _serializers.GetOrAdd(typeof(T), x => new XmlSerializer(x));
        }
    }
}
