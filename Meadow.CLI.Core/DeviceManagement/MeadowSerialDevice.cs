﻿using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Meadow.CLI.Core.Exceptions;
using Meadow.CLI.Core.Internals.MeadowCommunication;
using Microsoft.Extensions.Logging;

namespace Meadow.CLI.Core.DeviceManagement
{
    public partial class MeadowSerialDevice : MeadowLocalDevice
    {
        private readonly string _serialPortName;
        public SerialPort SerialPort { get; private set; }

        public MeadowSerialDevice(string serialPortName, ILogger? logger = null)
            : this(serialPortName, OpenSerialPort(serialPortName), logger)
        {
        }

        private MeadowSerialDevice(string serialPortName,
                                   SerialPort serialPort,
                                   ILogger? logger = null)
            : base(new MeadowSerialDataProcessor(serialPort, logger), logger)
        {
            SerialPort = serialPort;
            _serialPortName = serialPortName;
        }

        public override async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var then = now.Add(timeout);
            while (DateTime.UtcNow < then)
            {
                try
                {
                    var serialPort = await MeadowDeviceManager.FindMeadowComPortBySerialNumber(
                        DeviceInfo.SerialNumber,
                        Logger,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (serialPort == null)
                    {
                        throw new DeviceNotFoundException(
                            $"Unable to find Meadow with Serial Number {DeviceInfo.SerialNumber}");
                    }

                    SerialPort = new SerialPort(serialPort);
                    DeviceInfo = await GetDeviceInfoAsync(OneSecond, cancellationToken);
                    return;
                }
                catch (MeadowCommandException meadowCommandException)
                {
                    Logger.LogTrace(meadowCommandException, "Caught exception while waiting for device to be ready");
                }
                catch (Exception ex)
                {
                    Logger.LogTrace(ex, "Caught exception while waiting for device to be ready. Retrying.");
                }

                await Task.Delay(100, cancellationToken)
                          .ConfigureAwait(false);
            }

            throw new Exception($"Device not ready after {timeout}ms");
            
        }

        public sealed override bool IsDeviceInitialized()
        {
            return SerialPort != null;
        }

        public override void Dispose()
        {
            Logger.LogTrace("Disposing SerialPort");
            SerialPort.Dispose();
        }

        public override async Task WriteAsync(byte[] encodedBytes, int encodedToSend, CancellationToken cancellationToken = default)
        {
            if (SerialPort == null)
                throw new NotConnectedException();

            if (SerialPort.IsOpen == false)
            {
                Logger.LogDebug("Port is not open, attempting reconnect.");
                await AttemptToReconnectToMeadow(cancellationToken);
            }

            await SerialPort.BaseStream.WriteAsync(encodedBytes, 0, encodedToSend, cancellationToken).ConfigureAwait(false);
        }

        public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            var count = 0;
            while (!SerialPort.IsOpen)
            {
                SerialPort.Close();
                try
                {
                    SerialPort.Open();
                    await GetDeviceInfoAsync(DefaultTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await Task.Delay(100, cancellationToken)
                              .ConfigureAwait(false);
                }

                count++;
                if (count > 100)
                    throw new NotConnectedException();
            }

            return SerialPort.IsOpen;
        }

        private static SerialPort OpenSerialPort(string portName)
        {
            // Create a new SerialPort object with default settings
            var port = new SerialPort
                       {
                           PortName = portName,
                           BaudRate = 115200, // This value is ignored when using ACM
                           Parity = Parity.None,
                           DataBits = 8,
                           StopBits = StopBits.One,
                           Handshake = Handshake.None,

                           // Set the read/write timeouts
                           ReadTimeout = 5000,
                           WriteTimeout = 5000
                       };
            
            port.Open();
            port.BaseStream.ReadTimeout = 0;

            return port;
        }

        internal async Task<bool> AttemptToReconnectToMeadow(CancellationToken cancellationToken = default)
        {
            var delayCount = 20; // 10 seconds
            while (true)
            {
                await Task.Delay(500, cancellationToken)
                          .ConfigureAwait(false);

                var portOpened = await InitializeAsync(cancellationToken).ConfigureAwait(false);

                if (portOpened)
                {
                    Logger.LogDebug("Device successfully reconnected");
                    await Task.Delay(2000, cancellationToken)
                              .ConfigureAwait(false);

                    return true;
                }

                if (delayCount-- == 0)
                    throw new NotConnectedException();
            }
        }
    }
}