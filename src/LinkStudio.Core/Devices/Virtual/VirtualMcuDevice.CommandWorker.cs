using LinkStudio.Core.Bus;
using System.Globalization;
using System.Text;

namespace LinkStudio.Core.Devices.Virtual
{
    /// <summary>
    /// VirtualMcuDevice 的命令处理部分。
    /// partial 表示它会和其他文件中的 VirtualMcuDevice 合并成同一个类。
    /// </summary>
    public sealed partial class VirtualMcuDevice
    {
        private CancellationTokenSource? _timerCancellation;
        private Task? _timerTask;
        private long _tickCounter;

        private async Task CommandWorkerAsync()
        {
            // 这个 Worker 是命令队列的唯一读者，因此所有设备操作天然按顺序执行。
            Exception? failure = null;
            try
            {
                await foreach (var command in _commands.Reader.ReadAllAsync())
                {
                    try
                    {
                        switch (command)
                        {
                            case OpenVirtualMcuCommand open:
                                await HandleOpenAsync(open);
                                break;
                            case CloseVirtualMcuCommand close:
                                await HandleCloseAsync(close);
                                break;
                            case SendVirtualMcuCommand send:
                                await HandleSendAsync(send);
                                break;

                            case TickVirtualMcuCommand:
                            {
                                await HandleTickAsync();
                            } break;

                            case AutoVirtualMcuCommand autoSend:
                                await HandleAutoSend(autoSend);
                                break;

                            case SetSimulationModeVirtualMcuCommand setMode:
                                HandleSetSimulationMode(setMode);
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (GetToken(command).IsCancellationRequested)
                    {
                        // when 确认异常确实来自当前命令的取消信号，避免把程序错误误判为正常取消。
                        CompleteAsCanceled(command, GetToken(command));
                    }
                    catch (Exception error)
                    {
                        // 单条命令失败只填写这条命令的回执，不让整个命令 Worker 退出。
                        CompleteWithException(command, error);
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                // Worker 整体退出时，设备已无法继续工作，必须停止定时器并更新状态。
                await StopTimerAsync();
                Volatile.Write(ref _isOpen, 0);
                DrainPendingCommands(failure);
            }
        }

        private async Task HandleAutoSend(AutoVirtualMcuCommand autoSend)
        {
            autoSend.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                autoSend.Completion.TrySetException(new ObjectDisposedException(nameof(VirtualMcuDevice)));
                return;
            }

            if (IsOpen == false)
            {
                autoSend.Completion.TrySetException(new InvalidOperationException("请先打开虚拟设备。"));
                return;
            }

            if (autoSend.enabled)
            {
                if (autoSend.intervalMS == 0)
                {
                    autoSend.Completion.TrySetException(
                        new ArgumentOutOfRangeException(nameof(autoSend.intervalMS), "定时器周期必须大于 0 ms。"));
                    return;
                }

                // 先关再开，避免旧定时器泄漏，同时支持运行中直接修改周期。
                await StopTimerAsync();

                StartTimer(autoSend.intervalMS);
            }
            else
            {
                await StopTimerAsync();
            }

            autoSend.Completion.TrySetResult();
        }

        private async Task HandleOpenAsync(OpenVirtualMcuCommand command)
        {
            command.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                command.Completion.TrySetException(new ObjectDisposedException(nameof(VirtualMcuDevice)));
                return;
            }

            if (IsOpen)
            {
                command.Completion.TrySetResult();
                return;
            }

            // 先把 ready 包写入发布缓存（且发布阶段不接受外部取消），确认入队后再置 IsOpen = 1，
            // 避免入队中途取消导致“OpenAsync 失败但 IsOpen 却为 true”的状态撕裂。
            await QueuePacketAsync(
                PacketDirection.Rx,
                Encoding.UTF8.GetBytes("Virtual MCU ready"),
                command.Completion,
                command.CancellationToken,
                publishCt: CancellationToken.None);

            Volatile.Write(ref _isOpen, 1);
        }

        private async Task HandleCloseAsync(CloseVirtualMcuCommand command)
        {
            await StopTimerAsync();
            Volatile.Write(ref _isOpen, 0);

            // 空包是冲刷标记：发布 Worker 读到这里时，前面的包已全部进入 Bus。
            await _publishBuffer.Writer.WriteAsync(
                new PublishItem(null, command.Completion, CancellationToken.None));
        }

        private async Task HandleSendAsync(SendVirtualMcuCommand command)
        {
            command.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                command.Completion.TrySetException(new ObjectDisposedException(nameof(VirtualMcuDevice)));
                return;
            }

            if (!IsOpen)
            {
                command.Completion.TrySetException(
                    new InvalidOperationException("请先打开虚拟设备。"));
                return;
            }

            await QueuePacketAsync(
                PacketDirection.Tx, command.Payload, null, command.CancellationToken);

            // Rx 回显携带回执，它进入 Bus 后 SendAsync 才完成。
            await QueuePacketAsync(
                PacketDirection.Rx, command.Payload, command.Completion, command.CancellationToken);
        }

        private void HandleSetSimulationMode(SetSimulationModeVirtualMcuCommand command)
        {
            command.CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeStarted) == 1)
            {
                command.Completion.TrySetException(new ObjectDisposedException(nameof(VirtualMcuDevice)));
                return;
            }

            Volatile.Write(ref _simulationMode, (int)command.Mode);
            _tickCounter = 0;
            command.Completion.TrySetResult();
        }

        private async Task HandleTickAsync()
        {
            if (!IsOpen)
            {
                return;
            }

            var mode = (VirtualMcuSimulationMode)Volatile.Read(ref _simulationMode);
            var step = _tickCounter++;
            var payload = mode switch
            {
                VirtualMcuSimulationMode.SimulatedImuJson => BuildSimulatedImuJsonBytes(step),
                VirtualMcuSimulationMode.SimulatedGnssNmea => BuildSimulatedGnssNmeaBytes(step),
                _ => Encoding.UTF8.GetBytes("Virtual MCU tick")
            };

            await QueuePacketAsync(
                PacketDirection.Rx,
                payload,
                null,
                CancellationToken.None);
        }

        private static byte[] BuildSimulatedImuJsonBytes(long step)
        {
            // 用不同频率的正弦/余弦组合模拟 3 轴加速度、角速度与芯片温度，便于波形图和 KPI 卡验证。
            var t = step * 0.22;
            var ax = Math.Round(Math.Sin(t) * 1.45 + Math.Sin(t * 2.7) * 0.18, 3);
            var ay = Math.Round(Math.Cos(t * 0.85) * 1.15 - Math.Sin(t * 1.9) * 0.14, 3);
            var az = Math.Round(9.81 + Math.Sin(t * 0.5) * 0.35 + Math.Cos(t * 3.1) * 0.08, 3);
            var gx = Math.Round(Math.Sin(t * 1.3) * 28.5, 2);
            var temp = Math.Round(36.2 + Math.Sin(t * 0.15) * 1.4, 2);

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"imu\":{{\"ax\":{ax:F3},\"ay\":{ay:F3},\"az\":{az:F3},\"gx\":{gx:F2}}},\"temp\":{temp:F2}}}\r\n");
            return Encoding.UTF8.GetBytes(line);
        }

        private static byte[] BuildSimulatedGnssNmeaBytes(long step)
        {
            // 模拟多星座（GPS + 北斗 + GLONASS）3D 定位与缓慢移动的可见卫星，包含完整仰角、方位角和 SNR。
            var utc = DateTimeOffset.UtcNow.ToString("HHmmss.00", CultureInfo.InvariantCulture);
            var date = DateTimeOffset.UtcNow.ToString("ddMMyy", CultureInfo.InvariantCulture);
            var phase = step * 0.18;

            // 模拟在上海附近小幅移动，海拔和速度轻微波动。
            var latDeg = 31.2304 + Math.Sin(phase * 0.3) * 0.0004;
            var lonDeg = 121.4737 + Math.Cos(phase * 0.3) * 0.0005;
            var latNmea = FormatNmeaCoord(latDeg, isLatitude: true);
            var lonNmea = FormatNmeaCoord(lonDeg, isLatitude: false);
            var alt = 18.4 + Math.Sin(phase * 0.5) * 1.2;
            var hdop = 0.85 + Math.Sin(phase * 0.25) * 0.12;
            var pdop = 1.45 + Math.Cos(phase * 0.25) * 0.15;
            var speedKnots = 4.2 + Math.Sin(phase * 0.4) * 1.8;
            var course = (95.0 + step * 1.5) % 360.0;

            var sb = new StringBuilder(512);
            AppendNmeaSentence(sb, string.Create(
                CultureInfo.InvariantCulture,
                $"GNGGA,{utc},{latNmea},N,{lonNmea},E,1,11,{hdop:F2},{alt:F1},M,11.2,M,,"));
            AppendNmeaSentence(sb, string.Create(
                CultureInfo.InvariantCulture,
                $"GNRMC,{utc},A,{latNmea},N,{lonNmea},E,{speedKnots:F1},{course:F1},{date},,,A"));
            AppendNmeaSentence(sb, string.Create(
                CultureInfo.InvariantCulture,
                $"GNGSA,A,3,03,08,14,22,07,11,19,28,65,71,78,,{pdop:F2},{hdop:F2},1.18"));

            // 方位角随步数缓慢旋转，SNR 轻微起伏，让天空图和载噪比柱状图都能实时律动。
            var drift = (int)(step * 2 % 360);
            AppendNmeaSentence(sb, BuildGsvBody("GP", 1, 1, 4,
                ("03", 72, (28 + drift) % 360, ClampSnr(42 + Math.Sin(phase) * 3)),
                ("08", 48, (112 + drift) % 360, ClampSnr(37 + Math.Cos(phase * 1.1) * 4)),
                ("14", 31, (215 + drift) % 360, ClampSnr(33 + Math.Sin(phase * 0.7) * 3)),
                ("22", 18, (305 + drift) % 360, ClampSnr(27 + Math.Cos(phase * 0.9) * 3))));

            AppendNmeaSentence(sb, BuildGsvBody("BD", 1, 1, 4,
                ("07", 64, (160 + drift) % 360, ClampSnr(44 + Math.Cos(phase * 0.8) * 3)),
                ("11", 53, (55 + drift) % 360, ClampSnr(39 + Math.Sin(phase * 1.2) * 3)),
                ("19", 36, (258 + drift) % 360, ClampSnr(34 + Math.Cos(phase * 0.6) * 4)),
                ("28", 22, (340 + drift) % 360, ClampSnr(29 + Math.Sin(phase * 1.4) * 3))));

            AppendNmeaSentence(sb, BuildGsvBody("GL", 1, 1, 3,
                ("65", 58, (82 + drift) % 360, ClampSnr(36 + Math.Sin(phase * 0.9) * 3)),
                ("71", 40, (192 + drift) % 360, ClampSnr(31 + Math.Cos(phase * 1.3) * 3)),
                ("78", 25, (284 + drift) % 360, ClampSnr(25 + Math.Sin(phase * 0.5) * 4))));

            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static string FormatNmeaCoord(double decimalDegrees, bool isLatitude)
        {
            var abs = Math.Abs(decimalDegrees);
            var deg = (int)Math.Floor(abs);
            var min = (abs - deg) * 60.0;
            return isLatitude
                ? string.Create(CultureInfo.InvariantCulture, $"{deg:00}{min:00.0000}")
                : string.Create(CultureInfo.InvariantCulture, $"{deg:000}{min:00.0000}");
        }

        private static int ClampSnr(double value) => Math.Clamp((int)Math.Round(value), 12, 52);

        private static string BuildGsvBody(
            string talker,
            int totalMessages,
            int messageNumber,
            int satsInView,
            params (string Prn, int Elev, int Azim, int Snr)[] sats)
        {
            var sb = new StringBuilder();
            sb.Append(talker);
            sb.Append("GSV,");
            sb.Append(totalMessages);
            sb.Append(',');
            sb.Append(messageNumber);
            sb.Append(',');
            sb.Append(satsInView.ToString("00", CultureInfo.InvariantCulture));
            foreach (var sat in sats)
            {
                sb.Append(',');
                sb.Append(sat.Prn);
                sb.Append(',');
                sb.Append(sat.Elev.ToString("00", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(sat.Azim.ToString("000", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(sat.Snr.ToString("00", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static void AppendNmeaSentence(StringBuilder sb, string body)
        {
            byte checksum = 0;
            for (var i = 0; i < body.Length; i++)
            {
                checksum ^= (byte)body[i];
            }

            sb.Append('$');
            sb.Append(body);
            sb.Append('*');
            sb.Append(checksum.ToString("X2", CultureInfo.InvariantCulture));
            sb.Append("\r\n");
        }

        private void StartTimer(UInt32 intervalMS )
        {
            // CancellationTokenSource 相当于定时器的“停止开关”，Close 时会调用 Cancel。
            _timerCancellation = new CancellationTokenSource();
            _timerTask = TimerLoopAsync(intervalMS , _timerCancellation.Token);
        }

        private async Task TimerLoopAsync(UInt32 intervalMS, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMS));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    // 命令队列忙时跳过一次 Tick，避免定时消息不断积压。
                    _commands.Writer.TryWrite(new TickVirtualMcuCommand());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // CloseAsync 取消定时器属于正常停止。
            }
        }

        private async Task StopTimerAsync()
        {
            var cancellation = _timerCancellation;
            var timerTask = _timerTask;
            _timerCancellation = null;
            _timerTask = null;

            if (cancellation is null || timerTask is null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                await timerTask;
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private static CancellationToken GetToken(VirtualMcuCommand command)
        {
            // Open、Send 和 AutoSend 允许调用方取消；Close 和 Tick 返回永不取消的 None。
            return command switch
            {
                OpenVirtualMcuCommand open => open.CancellationToken,
                SendVirtualMcuCommand send => send.CancellationToken,
                AutoVirtualMcuCommand auto => auto.CancellationToken,
                SetSimulationModeVirtualMcuCommand setMode => setMode.CancellationToken,
                _ => CancellationToken.None
            };
        }

        private static void CompleteWithException(VirtualMcuCommand command, Exception error)
        {
            switch (command)
            {
                case OpenVirtualMcuCommand open:
                    open.Completion.TrySetException(error);
                    break;
                case CloseVirtualMcuCommand close:
                    close.Completion.TrySetException(error);
                    break;
                case SendVirtualMcuCommand send:
                    send.Completion.TrySetException(error);
                    break;

                case AutoVirtualMcuCommand autoVirtualMcuCommand:
                    autoVirtualMcuCommand.Completion.TrySetException(error);
                    break;

                case SetSimulationModeVirtualMcuCommand setMode:
                    setMode.Completion.TrySetException(error);
                    break;
            }
        }

        private static void CompleteAsCanceled(VirtualMcuCommand command, CancellationToken ct = default)
        {
            switch (command)
            {
                case OpenVirtualMcuCommand open:
                    open.Completion.TrySetCanceled(ct);
                    break;
                case CloseVirtualMcuCommand close:
                    close.Completion.TrySetCanceled(ct);
                    break;
                case SendVirtualMcuCommand send:
                    send.Completion.TrySetCanceled(ct);
                    break;

                case AutoVirtualMcuCommand autoVirtualMcuCommand:
                    autoVirtualMcuCommand.Completion.TrySetCanceled(ct);
                    break;

                case SetSimulationModeVirtualMcuCommand setMode:
                    setMode.Completion.TrySetCanceled(ct);
                    break;
            }
        }

        private void DrainPendingCommands(Exception? failure)
        {
            // Worker 已退出，队列里剩余命令不会再执行，必须结清回执避免调用方永久等待。
            while (_commands.Reader.TryRead(out var command))
            {
                if (failure is null)
                {
                    CompleteAsCanceled(command);
                }
                else
                {
                    CompleteWithException(command, failure);
                }
            }
        }
    }
}
