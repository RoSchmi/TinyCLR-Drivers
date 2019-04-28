using System;
using System.Collections;
using System.Net;
using System.Net.NetworkInterface;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Spi;
using GHIElectronics.TinyCLR.Drivers.STMicroelectronics.SPWF04Sx.Helpers;
using GHIElectronics.TinyCLR.Net.NetworkInterface;

namespace GHIElectronics.TinyCLR.Drivers.STMicroelectronics.SPWF04Sx {
    public class SPWF04SxInterface : NetworkInterface, ISocketProvider, ISslStreamProvider, IDnsProvider, IDisposable {
        private readonly ObjectPool commandPool;
        private readonly Hashtable netifSockets;
        private readonly Queue pendingCommands;
        private readonly ReadWriteBuffer readPayloadBuffer;
        private readonly SpiDevice spi;
        private readonly GpioPin irq;
        private readonly GpioPin reset;
        private SPWF04SxCommand activeCommand;
        private SPWF04SxCommand activeVariableLengthResponseCommand;
        private Thread worker;
        private bool running;
        private int nextSocketId;
        private bool SocketErrorHappened;

        public event SPWF04SxIndicationReceivedEventHandler IndicationReceived;
        public event SPWF04SxErrorReceivedEventHandler ErrorReceived;

        public SPWF04SxWiFiState State { get; private set; }
        public bool ForceSocketsTls { get; set; }
        public string ForceSocketsTlsCommonName { get; set; }

        public static SpiConnectionSettings GetConnectionSettings(SpiChipSelectType chipSelectType, int chipSelectLine) => new SpiConnectionSettings {
            ClockFrequency = 8000000,
            Mode = SpiMode.Mode0,
            DataBitLength = 8,
            ChipSelectType = chipSelectType,
            ChipSelectLine = chipSelectLine,
            ChipSelectSetupTime = TimeSpan.FromTicks(10 * 100)
        };

        public SPWF04SxInterface(SpiDevice spi, GpioPin irq, GpioPin reset) {
            this.commandPool = new ObjectPool(() => new SPWF04SxCommand());
            this.netifSockets = new Hashtable();
            this.pendingCommands = new Queue();
            this.readPayloadBuffer = new ReadWriteBuffer(32, 1500 + 512);
            this.spi = spi;
            this.irq = irq;
            this.reset = reset;

            this.State = SPWF04SxWiFiState.RadioTerminatedByUser;

            this.reset.SetDriveMode(GpioPinDriveMode.Output);
            this.reset.Write(GpioPinValue.Low);

            this.irq.SetDriveMode(GpioPinDriveMode.Input);

            NetworkInterface.RegisterNetworkInterface(this);
        }

        ~SPWF04SxInterface() => this.Dispose(false);

        public void Dispose() {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                this.TurnOff();

                this.spi.Dispose();
                this.irq.Dispose();
                this.reset.Dispose();

                NetworkInterface.DeregisterNetworkInterface(this);
            }
        }

        public void TurnOn() {
            if (this.running) return;

            this.running = true;
            this.worker = new Thread(this.Process);
            this.worker.Start();

            this.reset.SetDriveMode(GpioPinDriveMode.Input);
        }

        public void TurnOff() {
            if (!this.running) return;

            this.reset.SetDriveMode(GpioPinDriveMode.Output);
            this.reset.Write(GpioPinValue.Low);

            this.running = false;
            this.worker.Join();
            this.worker = null;

            this.pendingCommands.Clear();
            this.readPayloadBuffer.Reset();

            this.netifSockets.Clear();
            this.nextSocketId = 0;
            this.activeCommand = null;
            this.activeVariableLengthResponseCommand = null;

            this.commandPool.ResetAll();
        }

        protected SPWF04SxCommand GetCommand() => (SPWF04SxCommand)this.commandPool.Acquire();

        protected SPWF04SxCommand GetVariableLengthResponseCommand() {
            if (this.activeVariableLengthResponseCommand != null) throw new InvalidOperationException("Variable length response command already outstanding.");

            return this.activeVariableLengthResponseCommand = this.GetCommand();
        }

        protected void EnqueueCommand(SPWF04SxCommand cmd) {
            lock (this.pendingCommands) {
                this.pendingCommands.Enqueue(cmd);

                this.ReadyNextCommand();
            }
        }

        protected void FinishCommand(SPWF04SxCommand cmd) {
            if (this.activeCommand != cmd) throw new ArgumentException();

            lock (this.pendingCommands) {
                cmd.Reset();

                this.commandPool.Release(cmd);

                this.ReadyNextCommand();
            }
        }

        private void ReadyNextCommand() {
            lock (this.pendingCommands) {
                if (this.pendingCommands.Count != 0) {
                    var cmd = (SPWF04SxCommand)this.pendingCommands.Dequeue();
                    cmd.SetPayloadBuffer(this.readPayloadBuffer);
                    this.activeCommand = cmd;
                }
                else {
                    this.activeCommand = null;
                }
            }
        }

        public void ClearTlsServerRootCertificate() {
            var cmd = this.GetCommand()
                .AddParameter("content")
                .AddParameter("2")
                .Finalize(SPWF04SxCommandIds.TLSCERT);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();
            cmd.ReadBuffer();
            this.FinishCommand(cmd);
        }

        public string SetTlsServerRootCertificate(byte[] certificate) {
            if (certificate == null) throw new ArgumentNullException();

            var cmd = this.GetCommand()
                .AddParameter("ca")
                .AddParameter(certificate.Length.ToString())
                .Finalize(SPWF04SxCommandIds.TLSCERT, certificate, 0, certificate.Length);

            this.EnqueueCommand(cmd);

            var result = cmd.ReadString();

            cmd.ReadBuffer();

            this.FinishCommand(cmd);

            return result.Substring(result.IndexOf(':') + 1);
        }

        public string GetConfigurationVariable(string variable) {
            var cmd = this.GetCommand()
                .AddParameter(variable)
                .Finalize(SPWF04SxCommandIds.GCFG);

            this.EnqueueCommand(cmd);

            var result = cmd.ReadString();

            cmd.ReadBuffer();

            this.FinishCommand(cmd);

            return result;
        }

        public void SetConfigurationVariable(string variable, string value) {
            var cmd = this.GetCommand()
                .AddParameter(variable)
                .AddParameter(value)
                .Finalize(SPWF04SxCommandIds.SCFG);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public void SaveConfiguration() {
            var cmd = this.GetCommand()
                .Finalize(SPWF04SxCommandIds.WCFG);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public void ResetConfiguration() {
            var cmd = this.GetCommand()
                .Finalize(SPWF04SxCommandIds.FCFG);
            this.EnqueueCommand(cmd);
            cmd.ReadBuffer();
            this.FinishCommand(cmd);
        }

        public string GetTime() {
            var cmd = this.GetCommand()
                .Finalize(SPWF04SxCommandIds.TIME);

            this.EnqueueCommand(cmd);

            var a = cmd.ReadString();
            var b = cmd.ReadString();

            cmd.ReadBuffer();

            this.FinishCommand(cmd);

            return $"{a} {b}";
        }

        public string ComputeFileHash(SPWF04SxHashType hashType, string filename) {
            var cmd = this.GetCommand()
                .AddParameter(hashType == SPWF04SxHashType.Md5 ? "3" : hashType == SPWF04SxHashType.Sha256 ? "2" : hashType == SPWF04SxHashType.Sha224 ? "1" : "0")
                .AddParameter(filename)
                .Finalize(SPWF04SxCommandIds.HASH);

            this.EnqueueCommand(cmd);

            var result = cmd.ReadString();

            cmd.ReadBuffer();

            this.FinishCommand(cmd);

            return result;
        }

        public void MountVolume(SPWF04SxVolume volume) {
            var cmd = this.GetCommand()
                .AddParameter(volume == SPWF04SxVolume.ApplicationFlash ? "3" : volume == SPWF04SxVolume.Ram ? "2" : volume == SPWF04SxVolume.UserFlash ? "1" : "0")
                .Finalize(SPWF04SxCommandIds.FSM);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public void UnmountVolume(SPWF04SxVolume volume) {
            var cmd = this.GetCommand()
                .AddParameter(volume == SPWF04SxVolume.ApplicationFlash ? "3" : volume == SPWF04SxVolume.Ram ? "2" : volume == SPWF04SxVolume.UserFlash ? "1" : "0")
                .AddParameter("0")
                .Finalize(SPWF04SxCommandIds.FSU);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public void GetFileListing() {
            var cmd = this.GetVariableLengthResponseCommand()
               .Finalize(SPWF04SxCommandIds.FSL);

            this.EnqueueCommand(cmd);
        }

        public void CreateFile(string filename, byte[] data) => this.CreateFile(filename, data, 0, data != null ? data.Length : throw new ArgumentNullException(nameof(data)));
        public void CreateFile(string filename, byte[] data, int offset, int count) {
            if (filename == null) throw new ArgumentNullException();
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0) throw new ArgumentOutOfRangeException();
            if (count < 0) throw new ArgumentOutOfRangeException();
            if (offset + count > data.Length) throw new ArgumentOutOfRangeException();

            var cmd = this.GetCommand()
                .AddParameter(filename)
                .AddParameter(count.ToString())
                .Finalize(SPWF04SxCommandIds.FSC, data, offset, count);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();
        }

        public void DeleteFile(string filename) {
            if (filename == null) throw new ArgumentNullException();

            var cmd = this.GetCommand()
                .AddParameter(filename)
                .Finalize(SPWF04SxCommandIds.FSD);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public int ReadFile(string filename, byte[] buffer, int offset, int count) {
            var cmd = this.GetCommand()
                   .AddParameter(filename)
                   .AddParameter(offset.ToString())
                   .AddParameter(count.ToString())
                  .Finalize(SPWF04SxCommandIds.FSP);

            this.EnqueueCommand(cmd);

            var total = SPWF04SxInterface.ReadBuffer(cmd, buffer, offset, count);

            this.FinishCommand(cmd);

            return total;
        }

        public string SendPing(string host) => this.SendPing(host, 1, 56);
        public string SendPing(string host, int count, int packetSize) {
            var cmd = this.GetCommand()
                .AddParameter(count.ToString())
                .AddParameter(packetSize.ToString())
                .AddParameter(host)
                .Finalize(SPWF04SxCommandIds.PING);

            this.EnqueueCommand(cmd);

            var str = cmd.ReadString();

            cmd.ReadBuffer();
            cmd.ReadBuffer();
            cmd.ReadBuffer();

            return str.Split(':')[1];
        }

        public int SendHttpGet(string host, string path) => this.SendHttpGet(host, path, 80, SPWF04SxConnectionSecurityType.None);
        public int SendHttpGet(string host, string path, int port, SPWF04SxConnectionSecurityType connectionSecurity) => this.SendHttpGet(host, path, port, connectionSecurity, null, null);
        public int SendHttpGet(string host, string path, int port, SPWF04SxConnectionSecurityType connectionSecurity, string inputFile, string outputFile) {
            var cmd = (outputFile != null ? this.GetCommand() : this.GetVariableLengthResponseCommand())
                .AddParameter(host)
                .AddParameter(path)
                .AddParameter(port.ToString())
                .AddParameter(connectionSecurity == SPWF04SxConnectionSecurityType.None ? "0" : "2")
                .AddParameter(null)
                .AddParameter(null)
                .AddParameter(inputFile)
                .AddParameter(outputFile)
                .Finalize(SPWF04SxCommandIds.HTTPGET);

            this.EnqueueCommand(cmd);

            var result = cmd.ReadString();
            if (connectionSecurity == SPWF04SxConnectionSecurityType.Tls && result == string.Empty) {
                result = cmd.ReadString();

                if (result.IndexOf("Loading:") == 0)
                    result = cmd.ReadString();
            }

            return result.Split(':') is var parts && parts[0] == "Http Server Status Code" ? int.Parse(parts[1]) : throw new Exception($"Request failed: {result}");
        }

        public int SendHttpPost(string host, string path) => this.SendHttpPost(host, path, 80, SPWF04SxConnectionSecurityType.None);
        public int SendHttpPost(string host, string path, int port, SPWF04SxConnectionSecurityType connectionSecurity) => this.SendHttpPost(host, path, port, connectionSecurity, null, null);
        public int SendHttpPost(string host, string path, int port, SPWF04SxConnectionSecurityType connectionSecurity, string inputFile, string outputFile) {
            var cmd = (inputFile != null ? this.GetCommand() : this.GetVariableLengthResponseCommand())
                .AddParameter(host)
                .AddParameter(path)
                .AddParameter(port.ToString())
                .AddParameter(connectionSecurity == SPWF04SxConnectionSecurityType.None ? "0" : "2")
                .AddParameter(null)
                .AddParameter(null)
                .AddParameter(inputFile)
                .AddParameter(outputFile)
                .Finalize(SPWF04SxCommandIds.HTTPPOST);

            this.EnqueueCommand(cmd);

            var result = cmd.ReadString();
            if (connectionSecurity == SPWF04SxConnectionSecurityType.Tls && result == string.Empty) {
                result = cmd.ReadString();

                if (result.IndexOf("Loading:") == 0)
                    result = cmd.ReadString();
            }

            return result.Split(':') is var parts && parts[0] == "Http Server Status Code" ? int.Parse(parts[1]) : throw new Exception($"Request failed: {result}");
        }

        public int ReadResponseBody(byte[] buffer, int offset, int count) {
            if (this.activeVariableLengthResponseCommand == null) throw new InvalidOperationException();

            var len = this.activeVariableLengthResponseCommand.ReadBuffer(buffer, offset, count);

            if (len == 0) {
                this.FinishCommand(this.activeVariableLengthResponseCommand);

                this.activeVariableLengthResponseCommand = null;
            }

            return len;
        }

        public int OpenSocket(string host, int port, SPWF04SxConnectionType connectionType, SPWF04SxConnectionSecurityType connectionSecurity, string commonName = null) {
            var cmd = this.GetCommand()
                .AddParameter(host)
                .AddParameter(port.ToString())
                .AddParameter(null)
                .AddParameter(commonName ?? (connectionType == SPWF04SxConnectionType.Tcp ? (connectionSecurity == SPWF04SxConnectionSecurityType.Tls ? "s" : "t") : "u"))
                .Finalize(SPWF04SxCommandIds.SOCKON);

            SocketErrorHappened = false;
            this.EnqueueCommand(cmd);
            Thread.Sleep(0);
            string a = string.Empty;
            string b = string.Empty;
            if (!SocketErrorHappened)
            {
                a = cmd.ReadString();               
                Thread.Sleep(0);
                if (!SocketErrorHappened)
                {
                    b = cmd.ReadString();                    
                }
                Thread.Sleep(0);
                if (connectionSecurity == SPWF04SxConnectionSecurityType.Tls && b.IndexOf("Loading:") == 0)
                {
                    if (!SocketErrorHappened)
                    {
                        a = cmd.ReadString();                       
                    }
                    Thread.Sleep(0);
                    if (!SocketErrorHappened)
                    {
                        b = cmd.ReadString();                       
                    }
                }               
            }
            this.FinishCommand(cmd);
           
            // return -1 as socket No. if an error happened while opening a socket, so you can handle the result (retry or abort)
            // avoids occasional hangs
            return a.Split(':') is var result && result[0] == "On" ? int.Parse(result[2]) : -1;            
        }

        public void CloseSocket(int socket) {
            var cmd = this.GetCommand()
                .AddParameter(socket.ToString())
                .Finalize(SPWF04SxCommandIds.SOCKC);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public void WriteSocket(int socket, byte[] data) => this.WriteSocket(socket, data, 0, data != null ? data.Length : throw new ArgumentNullException(nameof(data)));

        public void WriteSocket(int socket, byte[] data, int offset, int count) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0) throw new ArgumentOutOfRangeException();
            if (count < 0) throw new ArgumentOutOfRangeException();
            if (offset + count > data.Length) throw new ArgumentOutOfRangeException();

            var cmd = this.GetCommand()
                .AddParameter(socket.ToString())
                .AddParameter(count.ToString())
                .Finalize(SPWF04SxCommandIds.SOCKW, data, offset, count);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public int ReadSocket(int socket, byte[] buffer, int offset, int count) {
            var cmd = this.GetCommand()
                .AddParameter(socket.ToString())
                .AddParameter(count.ToString())
                .Finalize(SPWF04SxCommandIds.SOCKR);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            var total = SPWF04SxInterface.ReadBuffer(cmd, buffer, offset, count);

            this.FinishCommand(cmd);

            return total;
        }

        public int QuerySocket(int socket) {
            var cmd = this.GetCommand()
                .AddParameter(socket.ToString())
                .Finalize(SPWF04SxCommandIds.SOCKQ);

            this.EnqueueCommand(cmd);

            var result = cmd.ReadString().Split(':');

            cmd.ReadBuffer();

            this.FinishCommand(cmd);

            return result[0] == "Query" ? int.Parse(result[1]) : throw new Exception("Request failed");
        }

        public void ListSockets() {
            var cmd = this.GetVariableLengthResponseCommand()
            .Finalize(SPWF04SxCommandIds.SOCKL);
        
            this.EnqueueCommand(cmd);
        }

        public void EnableRadio() {
            var cmd = this.GetCommand()
                .AddParameter("1")
                .Finalize(SPWF04SxCommandIds.WIFI);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public void DisableRadio() {
            var cmd = this.GetCommand()
                .AddParameter("0")
                .Finalize(SPWF04SxCommandIds.WIFI);

            this.EnqueueCommand(cmd);

            cmd.ReadBuffer();

            this.FinishCommand(cmd);
        }

        public void JoinNetwork(string ssid, string password) {
            this.DisableRadio();

            this.SetConfigurationVariable("wifi_mode", "1");
            this.SetConfigurationVariable("wifi_priv_mode", "2");
            this.SetConfigurationVariable("wifi_wpa_psk_text", password);

            var cmd = this.GetCommand()
                .AddParameter(ssid)
                .Finalize(SPWF04SxCommandIds.SSIDTXT);
            this.EnqueueCommand(cmd);
            cmd.ReadBuffer();
            this.FinishCommand(cmd);

            this.EnableRadio();

            this.SaveConfiguration();
        }

        private static int ReadBuffer(SPWF04SxCommand cmd, byte[] buffer, int offset, int count) {
            var current = 0;
            var total = 0;

            do {
                current = cmd.ReadBuffer(buffer, offset + total, count - total);
                total += current;
            } while (current != 0);

            return total;
        }

        private void Process() {
            var pendingEvents = new Queue();
            var windPayloadBuffer = new GrowableBuffer(32, 1500 + 512);
            var readHeaderBuffer = new byte[4];
            var syncRead = new byte[1];
            var syncWrite = new byte[1];

            while (this.running) {
                var hasWrite = this.activeCommand != null && !this.activeCommand.Sent;
                var hasIrq = this.irq.Read() == GpioPinValue.Low;

                if (hasIrq || hasWrite) {
                    syncWrite[0] = (byte)(!hasIrq && hasWrite ? 0x02 : 0x00);

                    this.spi.TransferFullDuplex(syncWrite, syncRead);

                    if (!hasIrq && hasWrite && syncRead[0] != 0x02) {
                        this.activeCommand.WriteHeader(this.spi.Write);

                        if (this.activeCommand.HasWritePayload) {
                            while (this.irq.Read() == GpioPinValue.High)
                                Thread.Sleep(0);

                            this.activeCommand.WritePayload(this.spi.Write);

                            while (this.irq.Read() == GpioPinValue.Low)
                                Thread.Sleep(0);
                        }

                        this.activeCommand.Sent = true;
                    }
                    else if (syncRead[0] == 0x02) {
                        this.spi.Read(readHeaderBuffer);

                        var status = readHeaderBuffer[0];
                        var ind = readHeaderBuffer[1];
                        var payloadLength = (readHeaderBuffer[3] << 8) | readHeaderBuffer[2];
                        var type = (status & 0b1111_0000) >> 4;

                        this.State = (SPWF04SxWiFiState)(status & 0b0000_1111);

                        if (type == 0x01 || type == 0x02) {
                            if (payloadLength > 0) {
                                windPayloadBuffer.EnsureSize(payloadLength, false);

                                this.spi.Read(windPayloadBuffer.Data, 0, payloadLength);
                            }

                            var str = Encoding.UTF8.GetString(windPayloadBuffer.Data, 0, payloadLength);

                            pendingEvents.Enqueue(type == 0x01 ? new SPWF04SxIndicationReceivedEventArgs((SPWF04SxIndication)ind, str) : (object)new SPWF04SxErrorReceivedEventArgs(ind, str));
                        }
                        else if (type == 0x03) {
                            if (this.activeCommand == null || !this.activeCommand.Sent) throw new InvalidOperationException("Unexpected payload.");

                            //See https://github.com/ghi-electronics/TinyCLR-Drivers/issues/10
                            //switch (ind) {
                            //    case 0x00://AT-S.OK without payload
                            //    case 0x03://AT-S.OK with payload
                            //    case 0xFF://AT-S.x not maskable
                            //    case 0xFE://AT-S.x maskable
                            //    default://AT-S.ERROR x
                            //        break;
                            //}

                            switch (ind)
                            {

                                case 0x00:
                                case 0x03:
                                case 0xFE:
                                case 0xFF:
                                    { }
                                    break;


                                case 0x2C:  // dec 44: wait for connection up
                                case 0x41:  // dec 65: DNS Address Failure
                                case 0x4A:  // dec 74: Failed to open socket
                                case 0x4C:  // dec 79: Write failed
                                    {
                                        // SocketErrorHappened is used in Opensocket cmd to handle errors when trying to open socket
                                        SocketErrorHappened = true;
                                        // make errors available outside
                                        pendingEvents.Enqueue(new SPWF04SxErrorReceivedEventArgs(ind, ""));
                                    }
                                    break;

                                default:
                                    {
                                        // make errors available outside
                                        pendingEvents.Enqueue(new SPWF04SxErrorReceivedEventArgs(ind, ""));
                                    }
                                    break;


                                    // here is an explanation of some error messages I saw when working with the module
                                    // should be deleted in final version
                                    //    case 0x02:
                                    //        {
                                    //            // Seen, actually meaning not clear
                                    //            Debug.WriteLine("Indication: " + ind.ToString("X2") + " PayLoad = " + payloadLength.ToString());
                                    //        }
                                    //        break;
                                    //
                                    //
                                    //    case 0x04:
                                    //       {
                                    //            // Seen after bad formed configuration command
                                    //            Debug.WriteLine("Indication: " + ind.ToString("X2") + " Wrong command format " + " Pld = " + payloadLength.ToString());                                       
                                    //        }
                                    //        break;
                                    //
                                    //    case 0x2C:  // dec 44: wait for connection up
                                    //        {
                                    //            SocketErrorHappened = true;
                                    //            Debug.WriteLine("Indication: " + ind.ToString("X2") + " Wait for connection up " + " Pld = " + payloadLength.ToString());
                                    //        }
                                    //        break;
                                    //    case 0x38:  // dec 56: Unable to delete file ?
                                    //        {                                      
                                    //            Debug.WriteLine("Ind: " + ind.ToString("X2") + " Unable to delete file" + " Pld = " + payloadLength.ToString());                                       
                                    //        }
                                    //        break;
                                    //    case 0x41:  // dez 65: DNS Address Failure
                                    //        {                                       
                                    //            SocketErrorHappened = true;
                                    //            Debug.WriteLine("Indication: " + ind.ToString("X2") + " DNS Address failed " + " Pld = " + payloadLength.ToString());
                                    //        }
                                    //        break;
                                    //
                                    //    case 0x4A:   // dec 74: Failed to open socket  
                                    //        {
                                    //            SocketErrorHappened = true;
                                    //            Debug.WriteLine("Ind: " + ind.ToString("X2") + " Failed to open socket " + " Pld = " + payloadLength.ToString());                                       
                                    //        }
                                    //        break;
                                    //    case 0x4C:   // dec 79: Write failed
                                    //        {
                                    //            SocketErrorHappened = true;
                                    //            Debug.WriteLine("Ind: " + ind.ToString("X2") + " Write failed " + " Pld = " + payloadLength.ToString());                                       
                                    //        }
                                    //        break;
                                    //    case 0x6F:   // dec 111 Request failed
                                    //        {
                                    //            Debug.WriteLine("Ind: " + ind.ToString("X2") + " Request failed " + " Pld = " + payloadLength.ToString());                                       
                                    //        }
                                    //        break;
                            }
                           
                            this.activeCommand.ReadPayload(this.spi.Read, payloadLength);
                        }
                        else {
                            throw new InvalidOperationException("Unexpected header.");
                        }
                    }
                }
                else {
                    while (pendingEvents.Count != 0) {
                        switch (pendingEvents.Dequeue()) {
                            case SPWF04SxIndicationReceivedEventArgs e: this.IndicationReceived?.Invoke(this, e); break;
                            case SPWF04SxErrorReceivedEventArgs e: this.ErrorReceived?.Invoke(this, e); break;
                        }
                    }
                }

                Thread.Sleep(0);
            }
        }

        private int GetInternalSocketId(int socket) => this.netifSockets.Contains(socket) ? (int)this.netifSockets[socket] : throw new ArgumentException();

        private void GetAddress(SocketAddress address, out string host, out int port) {
            port = 0;
            port |= address[2] << 8;
            port |= address[3] << 0;

            host = "";
            host += address[4] + ".";
            host += address[5] + ".";
            host += address[6] + ".";
            host += address[7];
        }

        int ISocketProvider.Create(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType) {
            if (addressFamily != AddressFamily.InterNetwork || socketType != SocketType.Stream || protocolType != ProtocolType.Tcp) throw new ArgumentException();

            var id = this.nextSocketId++;

            this.netifSockets.Add(id, 0);

            return id;
        }

        int ISocketProvider.Available(int socket) => this.QuerySocket(this.GetInternalSocketId(socket));

        void ISocketProvider.Close(int socket) {
            this.CloseSocket(this.GetInternalSocketId(socket));

            this.netifSockets.Remove(socket);
        }

        void ISocketProvider.Connect(int socket, SocketAddress address) {
            if (!this.netifSockets.Contains(socket)) throw new ArgumentException();
            if (address.Family != AddressFamily.InterNetwork) throw new ArgumentException();

            this.GetAddress(address, out var host, out var port);

            this.netifSockets[socket] = this.OpenSocket(host, port, SPWF04SxConnectionType.Tcp, this.ForceSocketsTls ? SPWF04SxConnectionSecurityType.Tls : SPWF04SxConnectionSecurityType.None, this.ForceSocketsTls ? this.ForceSocketsTlsCommonName : null);
        }

        int ISocketProvider.Send(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout) {
            if (flags != SocketFlags.None) throw new ArgumentException();

            this.WriteSocket(this.GetInternalSocketId(socket), buffer, offset, count);

            return count;
        }

        int ISocketProvider.Receive(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout) {
            if (flags != SocketFlags.None) throw new ArgumentException();
            if (timeout != Timeout.Infinite && timeout < 0) throw new ArgumentException();

            var end = (timeout != Timeout.Infinite ? DateTime.UtcNow.AddMilliseconds(timeout) : DateTime.MaxValue).Ticks;
            var sock = this.GetInternalSocketId(socket);
            var avail = 0;

            do {
                avail = this.QuerySocket(sock);

                Thread.Sleep(1);
            } while (avail == 0 && DateTime.UtcNow.Ticks < end);

            return avail > 0 ? this.ReadSocket(sock, buffer, offset, Math.Min(avail, count)) : 0;
        }

        bool ISocketProvider.Poll(int socket, int microSeconds, SelectMode mode) {
            switch (mode) {
                default: throw new ArgumentException();
                case SelectMode.SelectError: return false;
                case SelectMode.SelectWrite: return true;
                case SelectMode.SelectRead: return this.QuerySocket(this.GetInternalSocketId(socket)) != 0;
            }
        }

        void ISocketProvider.Bind(int socket, SocketAddress address) => throw new NotImplementedException();
        void ISocketProvider.Listen(int socket, int backlog) => throw new NotImplementedException();
        int ISocketProvider.Accept(int socket) => throw new NotImplementedException();
        int ISocketProvider.SendTo(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout, SocketAddress address) => throw new NotImplementedException();
        int ISocketProvider.ReceiveFrom(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout, ref SocketAddress address) => throw new NotImplementedException();

        void ISocketProvider.GetRemoteAddress(int socket, out SocketAddress address) => address = new SocketAddress(AddressFamily.InterNetwork, 16);
        void ISocketProvider.GetLocalAddress(int socket, out SocketAddress address) => address = new SocketAddress(AddressFamily.InterNetwork, 16);

        void ISocketProvider.GetOption(int socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte[] optionValue) {
            if (optionLevel == SocketOptionLevel.Socket && optionName == SocketOptionName.Type)
                Array.Copy(BitConverter.GetBytes((int)SocketType.Stream), optionValue, 4);
        }

        void ISocketProvider.SetOption(int socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte[] optionValue) {

        }

        int ISslStreamProvider.AuthenticateAsClient(int socketHandle, string targetHost, X509Certificate certificate, SslProtocols[] sslProtocols) => socketHandle;
        int ISslStreamProvider.AuthenticateAsServer(int socketHandle, X509Certificate certificate, SslProtocols[] sslProtocols) => throw new NotImplementedException();
        void ISslStreamProvider.Close(int handle) => ((ISocketProvider)this).Close(handle);
        int ISslStreamProvider.Read(int handle, byte[] buffer, int offset, int count, int timeout) => ((ISocketProvider)this).Receive(handle, buffer, offset, count, SocketFlags.None, timeout);
        int ISslStreamProvider.Write(int handle, byte[] buffer, int offset, int count, int timeout) => ((ISocketProvider)this).Send(handle, buffer, offset, count, SocketFlags.None, timeout);
        int ISslStreamProvider.Available(int handle) => ((ISocketProvider)this).Available(handle);

        void IDnsProvider.GetHostByName(string name, out string canonicalName, out SocketAddress[] addresses) {
            var cmd = this.GetCommand()
                .AddParameter(name)
                .AddParameter("80")
                .AddParameter(null)
                .AddParameter("t")
                .Finalize(SPWF04SxCommandIds.SOCKON);

            this.EnqueueCommand(cmd);

            var result = cmd.ReadString().Split(':');

            cmd.ReadBuffer();

            this.FinishCommand(cmd);

            var socket = result[0] == "On" ? int.Parse(result[2]) : throw new Exception("Request failed");

            this.CloseSocket(socket);

            canonicalName = "";
            addresses = new[] { new IPEndPoint(IPAddress.Parse(result[1]), 80).Serialize() };
        }

        public override string Id => nameof(SPWF04Sx);
        public override string Name => this.Id;
        public override string Description => string.Empty;
        public override OperationalStatus OperationalStatus => this.State == SPWF04SxWiFiState.ReadyToTransmit ? OperationalStatus.Up : OperationalStatus.Down;
        public override bool IsReceiveOnly => false;
        public override bool SupportsMulticast => false;
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Wireless80211;

        public override bool Supports(NetworkInterfaceComponent networkInterfaceComponent) => networkInterfaceComponent == NetworkInterfaceComponent.IPv4;
    }
}
