using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LDARtools.PhxAccess
{
    public class DataPolledEventArgs : EventArgs
    {
        public Dictionary<string, string> PhxProperties { get; protected set; }
        public float Ppm { get; protected set; }


        public DataPolledEventArgs(Dictionary<string, string> phxProperties, float ppm)
        {
            PhxProperties = phxProperties;
            Ppm = ppm;
        }

        public string GetPropertyOrDefault(string name)
        {
            if (PhxProperties == null || !PhxProperties.ContainsKey(name)) return null;

            return PhxProperties[name];
        }
    }

    public class ErrorEventArgs : EventArgs
    {
        public Exception Exception { get; set; }

        public ErrorEventArgs(Exception exception)
        {
            Exception = exception;
        }
    }

    public class UpdateFirmwareEventArgs : EventArgs
    {
        public string Message { get; }

        public UpdateFirmwareEventArgs(string message)
        {
            Message = message;
        }
    }

    public class GetLogsProgressEventArgs : EventArgs
    {
        public decimal Progress { get; set; }

        public GetLogsProgressEventArgs(decimal progress)
        {
            Progress = progress;
        }
    }

    public class WriteFlashProgressEventArgs : EventArgs
    {
        public int Progress { get; set; }

        public WriteFlashProgressEventArgs(int progress)
        {
            Progress = progress;
        }
    }

    public class ReadFlashProgressEventArgs : EventArgs
    {
        public int Progress { get; set; }

        public ReadFlashProgressEventArgs(int progress)
        {
            Progress = progress;
        }
    }

    public class Reconnect21NeededException : Exception
    {
    }

    /// <summary>
    /// This class handles all of the low level serial communication with a phx21.
    /// It was ported from a c++ project, which explains some of the way things
    /// are layed out and named.
    /// 
    /// To command a phx21 you have to send the correct command byte (they're all defined as CMD_FIDM_*)
    /// along with the appropriate command parameters struct. All (or most maybe?) commands will
    /// elicit a response from the phx21.
    /// 
    /// Command responses from the phx21 come back over serial and are also defined structs.
    /// All responses start with the byte SYNC_CODE_RES. The next byte is the response length and the
    /// third is the command byte (CMD_FIDM_*) that the response matches to. Subsequent data varies and is as
    /// defined in the struct that matches to the type of command sent.
    /// 
    /// For example, to get the firmware version number:
    /// Send:       Byte CMD_FIDM_CONFIGURATION_READ with a ConfigurationReadParams struct
    /// Receive:    A ConfigurationResponse struct where the first byte is SYNC_CODE_RES.
    ///             The second byte is length 41 which matches the length of the ConfigurationResponse struct
    ///             The third byte is CMD_FIDM_CONFIGURATION_READ.
    ///             The rest of the bytes are the data for the struct, one of which is the firmware version
    /// </summary>
    public sealed class Phx21
    {
        public int PollingInterval { get; private set; } = 250;

        public DateTime StatusDateTime { get; private set; }

        public Phx21Status CurrentStatus { get; private set; }

        public event EventHandler<DataPolledEventArgs> DataPolled;

        public event EventHandler<ErrorEventArgs> Error;

        public Phx21(IInputStream inputStream, IOutputStream outputStream)
        {
            _inputStream = inputStream;
            _outputStream = outputStream;

            Initialize();
        }

        /// <summary>
        /// This function gets the firmware version from the phx21.
        /// 
        /// SENDS: CMD_FIDM_CONFIGURATION_READ command with ConfigurationReadParams
        /// RECEIVES: a ConfigurationResponse. The version is a field in ConfigurationResponse
        /// </summary>
        /// <returns>The firmware version number</returns>
        public string GetFirmwareVersion()
        {
            PrintTrace();
            var pCmd = new ConfigurationReadParams();

            var nLength = (byte)Marshal.SizeOf(typeof(ConfigurationReadParams));
            var nCmd = CMD_FIDM_CONFIGURATION_READ;

            var sanity = 0;

            var response = new ConfigurationResponse();

            while (sanity < 10)
            {
                //WriteToLog("GetFirmwareVersion try #" + sanity);
                sanity++;
                var sw = Stopwatch.StartNew();

                response = SendAndReceive<ConfigurationResponse>(nCmd, GetBytes(pCmd), nLength, nLength, true, longTimeout);

                if (sw.ElapsedMilliseconds > warnTime)
                {
                    //WriteToLog("Warning: GetFirmwareVersion took " + sw.ElapsedMilliseconds + " milliseconds");
                }

                //WriteToLog($"Firmware version: {response.nVersion.ToString()}");

                return response.nVersion.ToString();
            }

            throw new Exception("Unable to read version");
        }

        public void StartPollingData(int intervalInMilliseconds)
        {
            PrintTrace($"interval = {intervalInMilliseconds}");

            PollingInterval = intervalInMilliseconds;

            PollingTimer = new Timer(PollingTimerCallback, null, PollingInterval, PollingInterval);
        }

        public void StopPollingData()
        {
            PrintTrace();
            try
            {
                if (PollingTimer != null)
                {
                    PollingTimer.Dispose();
                    PollingTimer = null;
                }

                if (LoggingTimer != null)
                {
                    LoggingTimer.Dispose();
                    LoggingTimer = null;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Ignites the Phx21
        /// </summary>
        public void IgniteOn()
        {
            Ignite(true);
        }

        public void IgniteOn(bool useSecondaryGlowPlug)
        {
            Ignite(true, useSecondaryGlowPlug ? (byte)1 : (byte)0);
        }

        /// <summary>
        /// Receives the current status of the Phx21
        /// 
        /// SENDS: CMD_FIDM_READ_DATA_EXTENDED with READ_DATA_PARAMS
        /// RECEIVES: DEFAULT_RESPONSE_EXTENDED which is then passed to GetStatusFromFidmStatusExtended()
        /// to get the Phx21Status that is returned
        /// </summary>
        /// <returns>The current status from a phx21</returns>
        public Phx21Status ReadDataExtended()
        {
            var pCmd = new READ_DATA_PARAMS();
            var Rsp = new DEFAULT_RESPONSE_EXTENDED();

            var nLength = (byte)Marshal.SizeOf(typeof(READ_DATA_PARAMS));
            var nCmd = CMD_FIDM_READ_DATA_EXTENDED;

            var tries = 10;

            while (tries > 0)
            {
                tries--;

                try
                {
                    var sw = Stopwatch.StartNew();

                    Rsp = SendAndReceive<DEFAULT_RESPONSE_EXTENDED>(nCmd, GetBytes(pCmd), nLength, nLength, true);

                    sw.Stop();

                    if (sw.ElapsedMilliseconds > warnTime)
                    {
                        //WriteToLog("Warning: ReadDataExtended took " + sw.ElapsedMilliseconds + " milliseconds");
                    }

                    return GetStatusFromFidmStatusExtended(Rsp.status);
                }
                catch (Exception ex)
                {
                    if (tries == 0) throw;

                    //WriteToLog("Error getting status, retrying");
                    //WriteExceptionToPhxLog(ex);
                    Task.Delay(50).Wait();
                }
            }

            throw new Exception("Unable to read status");
        }

        //Send this before disconnect
        public void SendGoodbye()
        {
            PrintTrace();
            var cmd = new READ_DATA_PARAMS();

            var cmdSize = (byte)Marshal.SizeOf(typeof(READ_DATA_PARAMS));
            var cmdId = CMD_FIDM_GOODBYE;

            _goodbyeSent = true;
            TransmitSerialCmd(cmdId, GetBytes(cmd), cmdSize, cmdSize, true);
        }

        private Timer PollingTimer;
        private Timer LoggingTimer;

        private bool _goodbyeSent = false;

        private ConcurrentQueue<BluetoothMessage> sendMessages = new ConcurrentQueue<BluetoothMessage>();

        private class MessageContainer
        {
            public DateTime Timestamp { get; set; }
            public byte[] Bytes { get; set; }
            public byte Type { get; set; }
        }

        private ConcurrentDictionary<byte, MessageContainer> receivedMessages = new ConcurrentDictionary<byte, MessageContainer>();


        #region Define Constants

        /// <summary>
        /// Sync codes, these signal the start of a new message
        /// </summary>
        private const byte SYNC_CODE_CMD = 0x5A;

        private const byte SYNC_CODE_RES = 0xA5;

        /// <summary>
        /// Field positions common to all messages received
        /// </summary>
        private const byte FIELD_SYNC_CODE = 0;

        private const byte FIELD_LENGTH_BYTES = 1;
        private const byte FIELD_CMD_ID = 2;

        /// <summary>
        /// command bytes
        /// </summary>
        private const byte CMD_FIDM_SET_SAMPLING_PARAMETERS = 0x04;

        private const byte CMD_FIDM_CONFIGURATION_READ = 0x0A;
        private const byte CMD_FIDM_INTEGRATION_CONTROL = 0x0C;
        private const byte CMD_FIDM_PUMP_AUX_1_CONTROL = 0x1B;
        private const byte CMD_FIDM_SET_PUMPA_CLOSED_LOOP = 0x1D;
        private const byte CMD_FIDM_SET_DEADHEAD_PARAMS = 0x1E;
        private const byte CMD_FIDM_AUTO_IGNITION_SEQUENCE = 0x20;
        private const byte CMD_FIDM_SET_CAL_H2PRES_COMPENSATION = 0x24;
        private const byte CMD_FIDM_READ_DATA_EXTENDED = 0x25;
        private const byte CMD_FIDM_GOODBYE = 0x26;

        private const byte STATUS_PUMP_A_ON = 0x01;
        private const byte STATUS_SOLENOID_A_ON = 0x04;
        private const byte STATUS_SOLENOID_B_ON = 0x08;

        private const byte RANGE_MODE_0_LO = 0;
        private const byte RANGE_MODE_1_MID = 1;
        private const byte RANGE_MODE_2_HI = 2;
        private const byte RANGE_MODE_3_MAX = 3;

        /// <summary>
        /// States for used while receiving data
        /// </summary>
        private const byte STATE_WAITING_FOR_SYNC_CODE = 0;

        private const byte STATE_WAITING_FOR_LENGTH = 1;
        private const byte STATE_WAITING_FOR_RESPONSE_ID = 2;
        private const byte STATE_WAITING_FOR_RESPONSE_DATA = 3;

        private const int longTimeout = 5000;
        private const int warnTime = 1000;
        private int junkDataCount = 0;

        #endregion Define Constants

        

        private void OnError(ErrorEventArgs e)
        {
            Error?.Invoke(this, e);
        }

        

        private readonly IInputStream _inputStream;
        private readonly IOutputStream _outputStream;

        
        public bool IsRunning { get; set; }
        public string Status { get; set; }
        public int UseAvgPerc { get; set; }
        public int LongAverageCount { get; set; }
        public int ShortAverageCount { get; set; }
        public int AverageCutoffPpm { get; set; }

        private byte _currentHardwareAvg = 10;

        private static PropertyInfo[] _phx21StatusProperties;

        

        private void Initialize()
        {
            UseAvgPerc = 10;
            LongAverageCount = 25;
            ShortAverageCount = 5;
            AverageCutoffPpm = 40;

            _phx21StatusProperties = typeof(Phx21Status).GetRuntimeProperties().ToArray();

            StartMessageHandler();

            InitPollingAndLoggingActions();

            try
            {
                TryUtils.Retry(() => SetSamplingParameters(RANGE_MODE_0_LO), 3, 100);

                //second to last is the # samples for hw averaging
                TryUtils.Retry(() => SetIntegrationControlParams(0, 1, 7, 50000, _currentHardwareAvg, 0), 3, 100);
                TryUtils.Retry(() => SetDeadHeadParams(true, 150, 100), 3, 100);
                TryUtils.Retry(() => SetCalH2PressureCompensation((long) (10000 * -0.3), (long) (10000 * 0.3)), 3, 100);

                ////WriteToLog("Initialization complete");
            }
            catch (Exception ex)
            {
                ////WriteToLog("Problem during inilization");
                ////WriteExceptionToPhxLog(ex);
                throw ex;
            }
        }

        private Task _sendTask = null;
        private Task _receiveTask = null;

        private bool _shutdownNow = false;
        private object _shutdownSync = new object();

        //Don't call this on the main thread since it uses Monitor.Wait()
        public void Shutdown()
        {
            _shutdownNow = true;

            lock (_shutdownSync)
            {
                while (_sendTask != null && _receiveTask != null)
                {
                    Monitor.Wait(_shutdownSync, 500);
                }
            }
        }

        private void StartMessageHandler()
        {
            if (_sendTask == null)
            {
                _sendTask = new Task(() =>
                {
                    var errorcount = 0;

                    try
                    {
                        while (!_shutdownNow || (_shutdownNow && _receiveTask != null))
                            try
                            {
                                while (sendMessages.Any())
                                {
                                    BluetoothMessage message;
                                    if (sendMessages.TryDequeue(out message))
                                    {
                                        _outputStream.Flush();
                                        _outputStream.Write(message.Bytes, message.Offest, message.Length);
                                        _outputStream.Flush();
                                    }
                                }

                                Task.Delay(10).Wait();

                                errorcount = 0;
                            }
                            catch (Exception ex)
                            {
                                if (_goodbyeSent) break;

                                //WriteToLog("Message thread error #" + errorcount);
                                //WriteExceptionToPhxLog(ex);
                                errorcount++;

                                Task.Run(() => { OnError(new ErrorEventArgs(ex)); });

                                if (errorcount > 10)
                                {
                                    //WriteToLog("Message thread shutting down because of errors");
                                    Task.Run(() => { OnError(new ErrorEventArgs(new Reconnect21NeededException())); });
                                    _shutdownNow = true;
                                    return;
                                }
                            }
                    }
                    finally
                    {
                        _sendTask = null;

                        lock (_shutdownSync)
                        {
                            Monitor.PulseAll(_shutdownSync);
                        }
                    }

                    //WriteToLog("Message thread shutting down");
                });

                _sendTask.Start();
                //WriteToLog("Send thread started");
            }

            if (_receiveTask == null)
            {
                _receiveTask = new Task(() =>
                {
                    var errorcount = 0;

                    try
                    {
                        while (!_shutdownNow || (_shutdownNow && inPollingAction))
                            try
                            {
                                var messageBytes = GetNextResponse();

                                if (messageBytes.Length > 2)
                                {
                                    var type = messageBytes[2];

                                    ////WriteToLog($"Received message with type {type} and length {messageBytes.Length}");

                                    var container = new MessageContainer
                                    {
                                        Bytes = messageBytes,
                                        Type = type,
                                        Timestamp = DateTime.Now
                                    };

                                    receivedMessages[type] = container;
                                }

                                Task.Delay(10).Wait();

                                errorcount = 0;
                            }
                            catch (Exception ex)
                            {
                                if (_goodbyeSent) break;

                                //WriteToLog("Receive thread error #" + errorcount);
                                //WriteExceptionToPhxLog(ex);
                                errorcount++;

                                Task.Run(() => { OnError(new ErrorEventArgs(ex)); });

                                if (errorcount > 10)
                                {
                                    //WriteToLog("Receive thread shutting down because of errors");
                                    Task.Run(() => { OnError(new ErrorEventArgs(new Reconnect21NeededException())); });
                                    _shutdownNow = true;
                                    return;
                                }
                            }

                        //WriteToLog("Receive thread shutting down");
                    }
                    finally
                    {
                        _receiveTask = null;

                        lock (_shutdownSync)
                        {
                            Monitor.PulseAll(_shutdownSync);
                        }
                    }
                });

                _receiveTask.Start();
                //WriteToLog("receive thread started");
            }
        }

        private byte[] GetResponse(byte key, int waitTime, DateTime sendTime)
        {
            var start = DateTime.Now;

            while ((DateTime.Now - start).TotalMilliseconds < waitTime)
            {
                if (receivedMessages.ContainsKey(key) && receivedMessages[key].Timestamp >= sendTime)
                    return receivedMessages[key].Bytes;

                Task.Delay(20).Wait(20);
            }


            throw new Exception($"GetResponse timed out after {DateTime.Now - start} waiting for a response with code {key}");
        }

        private void PrintTrace(string message = null, [CallerMemberName] string callingMethod = "")
        {
            //WriteToLog(string.IsNullOrEmpty(message) ? $"Entered {callingMethod}" : $"Entered {callingMethod} - {message}");
        }

        private bool inPollingAction = false;

        private void InitPollingAndLoggingActions()
        {
            pollingAction = () =>
            {
                if (inPollingAction) return;

                inPollingAction = true;

                try
                {
                    float ppm;
                    if (_shutdownNow) return;

                    CurrentStatus = ReadDataExtended();
                    ppm = (float) CurrentStatus.Ppm;

                    StatusDateTime = DateTime.Now;

                    if (!IsRunning)
                    {
                        if (CurrentStatus.IsIgnited)
                        {
                            IsRunning = true;
                            Status = "Ignited";
                        }
                    }
                    else
                    {
                        if (!CurrentStatus.IsIgnited)
                        {
                            IsRunning = false;
                            Status = "Connected";
                        }
                    }

                    var properties = new Dictionary<string, string>();


                    foreach (var phx21StatusProperty in _phx21StatusProperties) properties[phx21StatusProperty.Name] = phx21StatusProperty.GetValue(CurrentStatus).ToString();

                    Task.Run(() => { OnDataPolled(new DataPolledEventArgs(properties, ppm)); });
                }
                catch (Exception ex)
                {
                    //WriteExceptionToPhxLog(ex);
                }
                finally
                {
                    inPollingAction = false;
                }
            };
        }

        private int changeCount = 0;

        

        private void PollingTimerCallback(object stateInfo)
        {
            pollingAction();
        }

        /// <summary>
        /// Takes a Phx21Status and determines if the phx21 is ignited
        /// </summary>
        /// <param name="status">The status to check</param>
        /// <returns>A bool representing whether or not the phx21 is ignited</returns>
        private bool CheckIfIgnited(Phx21Status status)
        {
            return status.ThermoCouple > 75 && status.IsSolenoidAOn && status.IsPumpAOn;
        }

        


        private string GetHeaderLineForLog()
        {
            return "time logged,time received,lph2,BatteryVoltage,ChamberOuterTemp,IsPumpAOn,PpmStr,SamplePressure,TankPressure,ThermoCouple,PumpPower,FIDRange,PicoAmps,RawPpm,IsSolenoidAOn,IsSolenoidBOn";
        }

        /// <summary>
        /// Takes a Phx21Status and returns some of the parameters in a comma delimites string.
        /// Here's the format:
        /// {0} - current date
        /// {1} - AirPressure
        /// {2} - BatteryVoltage
        /// {3} - ChamberOuterTemp
        /// {4} - IsPumpAOn
        /// {5} - PpmStr
        /// {6} - SamplePressure
        /// {7} - TankPressure
        /// {8} - ThermoCouple
        /// {9} - PumpPower
        /// {10} - FIDRange 
        /// {11} - PicoAmps
        /// </summary>
        /// <param name="status">The status you want broken out into a string</param>
        /// <returns>A comma delimited string of some status fields</returns>
        private string GetLineForLog(Phx21Status status)
        {
            var message = $"{DateTime.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture)},{status.Timestamp},{status.AirPressure},{status.BatteryVoltage},{status.ChamberOuterTemp},{status.IsPumpAOn},{status.PpmStr},{status.SamplePressure},{status.TankPressure},{status.ThermoCouple},{status.PumpPower},{status.FIDRange},{status.PicoAmps},{status.RawPpm},{status.IsSolenoidAOn},{status.IsSolenoidBOn}";

            return message;
        }

        

        private void OnDataPolled(DataPolledEventArgs e)
        {
            DataPolled?.Invoke(this, e);
        }

        private Action pollingAction;

        

        private void SetIntegrationControlParams(byte nMode, byte nChargeMultiplier, byte nRange,
            uint nIntegrationTimeUs,
            byte nSamplesToAvg, byte nReportMode)
        {
            PrintTrace(nMode + ", " + nChargeMultiplier + ", " + nRange + ", " + nIntegrationTimeUs + ", " + nSamplesToAvg + ", " + nReportMode);
            var pCmd = new IntegrationControlParams();

            var nLength = (byte) Marshal.SizeOf(typeof(IntegrationControlParams));
            var nCmd = CMD_FIDM_INTEGRATION_CONTROL;

            pCmd.nMode = nMode;
            pCmd.nChargeMultiplier = nChargeMultiplier;
            pCmd.nRange = nRange;
            pCmd.nIntegrationTimeUs0 = DwordToByte0(nIntegrationTimeUs);
            pCmd.nIntegrationTimeUs1 = DwordToByte1(nIntegrationTimeUs);
            pCmd.nSamplesToAvg = nSamplesToAvg;
            pCmd.nReportMode = nReportMode;

            var sw = Stopwatch.StartNew();

            SendAndReceive<FIDM_STATUS>(nCmd, GetBytes(pCmd), nLength, nLength, true);

            if (sw.ElapsedMilliseconds > warnTime)
            {
                //WriteToLog("Warning: SetIntegrationControlParams took " + sw.ElapsedMilliseconds + " milliseconds");
            }
        }

        

        private void SetDeadHeadParams(bool enabled, ushort pressureLimit, ushort timeout)
        {
            PrintTrace();
            var cmd = new DeadheadParams();

            var nLength = (byte) Marshal.SizeOf(typeof(DeadheadParams));
            var nCmd = CMD_FIDM_SET_DEADHEAD_PARAMS;

            cmd.enable = enabled ? (byte) 1 : (byte) 0;
            cmd.pressure_low_limit_hPSI = pressureLimit;
            cmd.max_duration_msec = timeout;

            var sw = Stopwatch.StartNew();

            SendAndReceive<FIDM_STATUS>(nCmd, GetBytes(cmd), nLength, nLength, true);

            if (sw.ElapsedMilliseconds > warnTime)
            {
                //WriteToLog("Warning: SetDeadHeadParams took " + sw.ElapsedMilliseconds + " milliseconds");
            }
        }

        /// <summary>
        /// Sets the LPH2 compensation.
        /// 
        /// SENDS: CMD_FIDM_SET_CAL_H2PRES_COMPENSATION with CalH2PressureCompensation params
        /// RECEIVES: FIDM_STATUS. Response is ignored.
        /// </summary>
        /// <param name="h2CompensationPos">LPH2 positive compensation</param>
        /// <param name="h2CompensationNeg">LPH2 negative compensation</param>
        private void SetCalH2PressureCompensation(long h2CompensationPos, long h2CompensationNeg)
        {
            PrintTrace();
            var cmd = new CalH2PressureCompensation();

            var nLength = (byte) Marshal.SizeOf(typeof(CalH2PressureCompensation));
            var nCmd = CMD_FIDM_SET_CAL_H2PRES_COMPENSATION;

            cmd.H2_compensation_pos = h2CompensationPos;
            cmd.H2_compensation_neg = h2CompensationNeg;
            cmd.spare_for_alignment = 0;

            var sw = Stopwatch.StartNew();

            SendAndReceive<FIDM_STATUS>(nCmd, GetBytes(cmd), nLength, nLength, true);

            if (sw.ElapsedMilliseconds > warnTime)
            {
                //WriteToLog("Warning: SetCalH2PressureCompensation took " + sw.ElapsedMilliseconds + " milliseconds");
            }
        }

        private void TransmitSerialData(byte nCurrChkSum, byte[] pBuffer, byte nLength, bool bSendCRC)
        {
            var nCRC = ReComputeCRC(nCurrChkSum, pBuffer, nLength);

            sendMessages.Enqueue(new BluetoothMessage
            {
                Bytes = pBuffer,
                Offest = 0,
                Length = nLength
            });

            if (bSendCRC)
                sendMessages.Enqueue(new BluetoothMessage
                {
                    Bytes = new byte[] {nCRC},
                    Offest = 0,
                    Length = 1
                });
        }

        private byte ReComputeCRC(byte nCurrChkSum, byte[] pStream, byte nLength)
        {
            for (var ii = 0; ii < nLength; ii++)
            {
                nCurrChkSum = (byte) ((nCurrChkSum << 1) | (nCurrChkSum >> 7));
                nCurrChkSum += pStream[ii];
            }

            return nCurrChkSum;
        }

        private void SetSamplingParameters(byte nFIDMRange)
        {
            PrintTrace(nFIDMRange.ToString());
            var pCmd = new SetSamplingParams();

            var nLength = (byte) Marshal.SizeOf(typeof(SetSamplingParams));
            var nCmd = CMD_FIDM_SET_SAMPLING_PARAMETERS;

            pCmd.nRange = nFIDMRange;

            var sw = Stopwatch.StartNew();

            SendAndReceive<FIDM_STATUS>(nCmd, GetBytes(pCmd), nLength, nLength, true);

            if (sw.ElapsedMilliseconds > warnTime)
            {
                //WriteToLog("Warning: SetSamplingParameters took " + sw.ElapsedMilliseconds + " milliseconds");
            }
        }

        private int num0s = 0;
        private int ignitedChagedCount = 0;
        private bool prevIgnite = false;
        private bool firstIgniteCheck = true;

        /// <summary>
        /// Takes a FIDM_STATUS_EXTENDED and creates a Phx21Status from it.
        /// 
        /// This is where junk data is filtered:
        /// if ((phx21Status.BatteryVoltage &gt; 15 || phx21Status.PicoAmps &lt; -10000 || phx21Status.ThermoCouple &lt; -400) and junkDataCount &lt; 10)
        /// its junk and will try to read status again.
        /// 
        /// This is also where it is determined if a phx21 is ignited if CheckIfIgnited() for this status and the last 3 status indicate ignition.
        /// 
        /// PPM ranging also happens in this function (wow, there's lots of fun stuff in here, huh?)
        /// 
        /// And ppm value averaging happens here as well
        /// 
        /// Check for pump power level > 85% is here too, shuts off pump
        /// 
        /// </summary>
        /// <param name="status">The status to convert</param>
        /// <returns>a Phx21Status from the status passed in</returns>
        private Phx21Status GetStatusFromFidmStatusExtended(FIDM_STATUS_EXTENDED status)
        {
            var ppm =
                Math.Round(
                    0.1f *
                    BytesToDword(status.nFIDTenthsPPM3, status.nFIDTenthsPPM2, status.nFIDTenthsPPM1,
                        status.nFIDTenthsPPM0), 1);

            if (ppm >= 100)
                ppm = Math.Round(ppm, 0);

            if (ppm < 0)
                ppm = 0;

            if (ppm == 0)
            {
                num0s++;

                if (num0s > 5) num0s = -5;

                if (num0s < 0) ppm = 0.1;
            }

            var phx21Status = new Phx21Status
            {
                IsPumpAOn = (status.nStatusFlags & STATUS_PUMP_A_ON) > 0,
                AirPressure = BytesToWord(status.nAirPressure_HPSI1, status.nAirPressure_HPSI0) / 100.0f,
                BatteryVoltage = BytesToWord(status.nBatt_mV1, status.nBatt_mV0) / 1000.0f,
                ChamberOuterTemp =
                    ConvertKelvinToFahrenheit(BytesToWord(status.nChamberOuterTemp_TK1, status.nChamberOuterTemp_TK0) /
                                              10.0f),
                RawPpm = ppm,
                SamplePressure = BytesToWord(status.nSamplePressure_HPSI1, status.nSamplePressure_HPSI0) / 100.0f,
                TankPressure = 10.0f * (BytesToWord(status.nH2Pressure_PSI1, status.nH2Pressure_PSI0) / 10),
                //this is copied... losing a fraction looks intentional
                ThermoCouple =
                    ConvertKelvinToFahrenheit(BytesToWord(status.nThermocouple_TK1, status.nThermocouple_TK0) / 10.0f),
                PicoAmps =
                    (double)
                    BytesToDword(status.nFIDTenthsPicoA_In13, status.nFIDTenthsPicoA_In12,
                        status.nFIDTenthsPicoA_In11, status.nFIDTenthsPicoA_In10) / (double) 10.0,
                SystemCurrent = BytesToWord(status.nSystemCurrentMa1, status.nSystemCurrentMa0),
                PumpPower = status.nPumpA_power_pct,
                IsSolenoidAOn = (status.nStatusFlags & STATUS_SOLENOID_A_ON) > 0,
                IsSolenoidBOn = (status.nStatusFlags & STATUS_SOLENOID_B_ON) > 0,
                FIDRange = status.nFIDRange,
                Timestamp = DateTime.UtcNow.ToString()
            };

            //check for ignition
            var isIgnited = CheckIfIgnited(phx21Status);

            if (!firstIgniteCheck)
            {
                if (isIgnited != prevIgnite)
                {
                    ignitedChagedCount++;

                    if (ignitedChagedCount >= 3)
                        //WriteToLog((isIgnited ? "Igited!" : "Extinguished"));
                        prevIgnite = isIgnited;
                }
                else
                {
                    ignitedChagedCount = 0;
                }

                phx21Status.IsIgnited = prevIgnite;
            }
            else
            {
                //WriteToLog($"First status: {(isIgnited ? "Igited!" : "Not Ignited")}");
                firstIgniteCheck = false;
                prevIgnite = isIgnited;
                phx21Status.IsIgnited = isIgnited;
            }

            //Check for junk data
            //Reread if junk data
            if ((phx21Status.BatteryVoltage > 15 || phx21Status.PicoAmps < -10000 || phx21Status.ThermoCouple < -400 || phx21Status.PumpPower > 100) && junkDataCount < 10)
            {
                //WriteToLog("Suspect data #" + junkDataCount + " received. Suspect status follows. Retrying.");
                //WriteToLog(GetLineForLog(phx21Status));
                Task.Delay(10).Wait();
                junkDataCount++;
                throw new Exception("Suspect data received!");
            }

            junkDataCount = 0;

            if (phx21Status.IsIgnited && phx21Status.PumpPower >= 85.0)
            {
                //WriteToLog("Pump power is above 85% (" + phx21Status.PumpPower + "%), shutting off pump!");
                TurnOffPump();
                OnError(new ErrorEventArgs(new Exception("Pump power is above 85% (" + phx21Status.PumpPower + "%)")));
            }

            //This is where the ppm range is switched
            if (phx21Status.FIDRange == RANGE_MODE_0_LO && phx21Status.PicoAmps >= 6500)
            {
                changeCount++;

                if (changeCount >= 1)
                {
                    changeCount = 0;
                    SetSamplingParameters(RANGE_MODE_3_MAX);
                    Task.Delay(250).Wait();
                }
            }
            else if (phx21Status.FIDRange == RANGE_MODE_3_MAX && phx21Status.PicoAmps <= 6000)
            {
                changeCount++;

                if (changeCount >= 1)
                {
                    changeCount = 0;
                    SetSamplingParameters(RANGE_MODE_0_LO);
                    Task.Delay(250).Wait();
                }
            }

            pastPpms.Enqueue(phx21Status.RawPpm);

            double disregard;
            while (pastPpms.Count > maxPastPpms)
                pastPpms.TryDequeue(out disregard);

            //apply averaging to the ppm value
            phx21Status.LongAveragePpm = pastPpms.Skip(Math.Max(pastPpms.Count - LongAverageCount, 0)).Average();

            phx21Status.LongAveragePpm = phx21Status.LongAveragePpm >= 100
                ? Math.Round(phx21Status.LongAveragePpm, 1)
                : Math.Round(phx21Status.LongAveragePpm, 0);

            var shortAveragePpms = pastPpms.Skip(Math.Max(pastPpms.Count - ShortAverageCount, 0)).ToArray();

            phx21Status.ShortAveragePpm = shortAveragePpms.Average();

            phx21Status.ShortAveragePpm = phx21Status.ShortAveragePpm >= 100
                ? Math.Round(phx21Status.ShortAveragePpm, 0)
                : Math.Round(phx21Status.ShortAveragePpm, 1);

            phx21Status.UseAverage = shortAveragePpms
                .All(p => p / phx21Status.LongAveragePpm * 100 >= 100 - UseAvgPerc
                          && p / phx21Status.LongAveragePpm * 100 <= 100 + UseAvgPerc);

            if (phx21Status.UseAverage)
                phx21Status.Ppm = phx21Status.FIDRange == RANGE_MODE_3_MAX ? phx21Status.LongAveragePpm : phx21Status.ShortAveragePpm;
            else
                phx21Status.Ppm = phx21Status.RawPpm;

            phx21Status.PpmStr = phx21Status.IsIgnited ? phx21Status.Ppm.ToString() : "N/A";

            if (phx21Status.PicoAmps <= 100 && _currentHardwareAvg == 10)
            {
                _currentHardwareAvg = 50;
                SetIntegrationControlParams(0, 1, 7, 50000, _currentHardwareAvg, 0);
            }
            else if (phx21Status.PicoAmps > 100 && _currentHardwareAvg == 50)
            {
                _currentHardwareAvg = 10;
                SetIntegrationControlParams(0, 1, 7, 50000, _currentHardwareAvg, 0);
            }

            //WriteToLog($"Received a status message: ppm {phx21Status.PpmStr}, raw ppm {phx21Status.RawPpm}, pA {phx21Status.PicoAmps}, {(phx21Status.IsIgnited ? "ignited" : "not ignited")}");
            return phx21Status;
        }

        private void TurnOffPump()
        {
            SetPumpACtrlLoop(false, 0);
            ControlPumpAux1(0, 0, 0);
        }

        private void SetPumpACtrlLoop(bool enable, long target)
        {
            var Cmd = new PumpClosedLoop();

            var nLength = (byte) Marshal.SizeOf(typeof(PumpClosedLoop));
            var nCmd = CMD_FIDM_SET_PUMPA_CLOSED_LOOP;

            Cmd.enable = enable ? (byte) 1 : (byte) 0;
            Cmd.target_hPSI = (short) target;

            //WriteToLog("SetPumpACtrlLoop");
            TransmitSerialCmd(nCmd, GetBytes(Cmd), nLength, nLength, true);
        }

        private void ControlPumpAux1(byte nId, uint nPowerLevelTenthsPercent, byte nKickStartDurationSec)
        {
            var pCmd = new PumpAux1ControlParams();

            var nLength = (byte) Marshal.SizeOf(typeof(PumpAux1ControlParams));
            var nCmd = CMD_FIDM_PUMP_AUX_1_CONTROL;

            pCmd.nID = nId;
            pCmd.nPowerTenthsPercent0 = DwordToByte0(nPowerLevelTenthsPercent);
            pCmd.nPowerTenthsPercent1 = DwordToByte1(nPowerLevelTenthsPercent);
            pCmd.nKickStartDurationSec = nKickStartDurationSec;

            //WriteToLog("ControlPumpAux1");
            TransmitSerialCmd(nCmd, GetBytes(pCmd), nLength, nLength, true);
        }

        private ConcurrentQueue<double> pastPpms = new ConcurrentQueue<double>();
        private int maxPastPpms = 50;

        private float ConvertKelvinToFahrenheit(float kelvin)
        {
            return (float) Math.Round((kelvin - 273.15f) * 1.8f + 32, 1);
        }

        private byte DwordToByte0(uint dword)
        {
            return (byte) (0xFF & dword);
        }

        private byte DwordToByte1(uint dword)
        {
            return (byte) (0xFF & (dword >> 8));
        }

        private byte DwordToByte2(uint dword)
        {
            return (byte) (0xFF & (dword >> 16));
        }

        private byte DwordToByte3(uint dword)
        {
            return (byte) (0xFF & (dword >> 24));
        }

        private int BytesToWord(byte b1, byte b0)
        {
            return (0xFFFF & (int) (b1 << 8)) | (int) b0;
        }

        private int BytesToDword(byte b3, byte b2, byte b1, byte b0)
        {
            return (int) (0xFFFFFFFF & ((int) (b3 << 24) | (int) (b2 << 16) | (int) (b1 << 8) | (int) b0));
        }

        private void Ignite(bool onOff)
        {
            Ignite(onOff, 0);
        }

        /// <summary>
        /// Ignites the phx21
        /// SENDS: CMD_FIDM_AUTO_IGNITION_SEQUENCE with AUTO_IGNITION_SEQUENCE args
        /// RECEIVES: FIDM_STATUS. Response is ignored.
        /// </summary>
        /// <param name="onOff">true to ignite, false to extinguish - extinguish doesn't seem to be used, call IgniteOff() instead</param>
        /// <param name="glowplug">true to use glow plug B, false to use glow plug A</param>
        private void Ignite(bool onOff, byte glowplug)
        {
            PrintTrace(onOff + ", " + glowplug);
            var nCmd = CMD_FIDM_AUTO_IGNITION_SEQUENCE;

            var ignition = BuildAutoIgnitionSequence();
            ignition.use_glow_plug_b = glowplug;
            ignition.start_stop = (byte) (onOff ? 1 : 0);

            var bytes = GetBytes(ignition);
            var nLength = (byte) bytes.Length;

            var sw = Stopwatch.StartNew();

            TransmitSerialCmd(nCmd, bytes, nLength, nLength, true);
            //this could be send and receive since the phx does send a response, but we don't care about it and it takes a really long time

            if (sw.ElapsedMilliseconds > warnTime)
            {
                //WriteToLog("Warning: Ignite took " + sw.ElapsedMilliseconds + " milliseconds");
            }
        }


        /// <summary>
        /// This is used to build the AUTO_IGNITION_SEQUENCE arguments for igniting a phx21
        /// </summary>
        /// <returns>A fully built AUTO_IGNITION_SEQUENCE</returns>
        private static AUTO_IGNITION_SEQUENCE BuildAutoIgnitionSequence()
        {
            var ignition = new AUTO_IGNITION_SEQUENCE();
            ignition.start_stop = 1;
            ignition.target_hPSI = 175;
            ignition.tolerance_hPSI = 5;
            ignition.max_pressure_wait_msec = 10000;
            ignition.min_temperature_rise_tK = 10;
            ignition.max_ignition_wait_msec = 5000;
            ignition.sol_b_delay_msec = 1000;
            ignition.use_glow_plug_b = 0;
            ignition.pre_purge_pump_msec = 5000;
            ignition.pre_purge_sol_A_msec = 5000;

            ignition.param1 = 0;
            ignition.param2 = 0;
            return ignition;
        }

        private T SendAndReceive<T>(byte key, byte[] message, byte commandLength, byte headerLength, bool sendCRC, int timeout = 2000) where T
            : new()
        {
            var sendTime = DateTime.Now;
            TransmitSerialCmd(key, message, commandLength, headerLength, sendCRC);

            bool success;
            var result = ReceiveCmdResponse<T>(key, timeout, sendTime, out success);

            if (!success) throw new Exception($"Could not get response for command type {key}");

            return result;
        }

        /// <summary>
        /// A wrapper around GetResponse() that formats the response bytes as a message of type T
        /// </summary>
        /// <typeparam name="T">The type of response you wish to receive</typeparam>
        /// <param name="key">The command byte that was just sent</param>
        /// <param name="waitTime">how long to wait in milliseconds</param>
        /// <param name="sendTime">the time the command was sent</param>
        /// <param name="success">out param indicating if the response was received in the specified waitTime</param>
        /// <returns>A response message of type T</returns>
        private T ReceiveCmdResponse<T>(byte key, int waitTime, DateTime sendTime, out bool success) where T
            : new()
        {
            success = false;

            var bytes = GetResponse(key, waitTime, sendTime);

            try
            {
                var rsp = FromBytes<T>(bytes);

                success = true;
                return rsp;
            }
            catch (Exception ex)
            {
                throw new Exception("Error parsing CmdResponse.", ex);
            }
        }

        private byte[] GetNextResponse()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var currentState = STATE_WAITING_FOR_SYNC_CODE;
            byte numBytesReceived = 0;
            var responseBytes = new List<byte>();
            var continueReading = true;

            while (continueReading)
            {
                byte readByte;

                readByte = _inputStream.ReadByte();

                switch (currentState)
                {
                    case STATE_WAITING_FOR_SYNC_CODE:
                        if (readByte == SYNC_CODE_RES)
                        {
                            numBytesReceived = 0;
                            numBytesReceived++;
                            if (responseBytes.Any()) responseBytes = new List<byte>();
                            responseBytes.Add(readByte);
                            currentState = STATE_WAITING_FOR_LENGTH;
                        }

                        break;

                    case STATE_WAITING_FOR_LENGTH:
                        numBytesReceived++;
                        responseBytes.Add(readByte);
                        currentState = STATE_WAITING_FOR_RESPONSE_ID;

                        if (readByte < 3)
                            //WriteToLog("GetResponse: Could not parse message: Length is " + readByte);
                            //WriteToLog("GetResponse: Waiting for sync code");
                            currentState = STATE_WAITING_FOR_SYNC_CODE;

                        break;

                    case STATE_WAITING_FOR_RESPONSE_ID:
                        numBytesReceived++;

                        responseBytes.Add(readByte);
                        currentState = STATE_WAITING_FOR_RESPONSE_DATA;
                        break;

                    case STATE_WAITING_FOR_RESPONSE_DATA:
                        numBytesReceived++;
                        responseBytes.Add(readByte);

                        if (numBytesReceived >= responseBytes[FIELD_LENGTH_BYTES])
                            // Receive all the command data as indicated by the count field.
                        {
                            currentState = STATE_WAITING_FOR_SYNC_CODE; // Go back to waiting for the sync code.
                            continueReading = false;
                        }

                        break;

                    default:
                        //WriteToLog("GetResponse: Could not parse message: bad state");
                        //WriteToLog("GetResponse: Waiting for sync code");
                        currentState = STATE_WAITING_FOR_SYNC_CODE;
                        break;
                }
            }

            return responseBytes.ToArray();
        }

        /// <summary>
        /// This is the workhorse function used to send serial commands.
        /// Sets the first 3 bytes to SYNC_CODE_CMD, message length, and cmd id
        /// and optionally adds a crc to the end
        /// </summary>
        /// <param name="nCmd">The command byte to be send, should be one of CMD_FIDM_*</param>
        /// <param name="pStream">The struct defining the data to send</param>
        /// <param name="nTotalCmdLength"></param>
        /// <param name="nHeaderLength">usually the same as nTotalCmdLength</param>
        /// <param name="bSendCrc">true to send the crc at the end of the message</param>
        /// <returns>The crc of the message sent</returns>
        private byte TransmitSerialCmd(byte nCmd, byte[] pStream, byte nTotalCmdLength,
            byte nHeaderLength, bool bSendCrc)
        {
            try
            {
                byte nCRC = 0;
                var pData = new byte[nHeaderLength + 1];

                pStream[FIELD_SYNC_CODE] = SYNC_CODE_CMD;
                pStream[FIELD_LENGTH_BYTES] = (byte) (nTotalCmdLength + 1);
                pStream[FIELD_CMD_ID] = nCmd;

                nCRC = ComputeCRC(pStream, nHeaderLength);

                Array.Copy(pStream, pData, nHeaderLength);

                if (bSendCrc)
                {
                    pData[nHeaderLength] = nCRC;

                    sendMessages.Enqueue(new BluetoothMessage
                    {
                        Bytes = pData,
                        Offest = 0,
                        Length = nHeaderLength + 1
                    });
                }
                else
                {
                    sendMessages.Enqueue(new BluetoothMessage
                    {
                        Bytes = pData,
                        Offest = 0,
                        Length = nHeaderLength
                    });
                }

                return nCRC;
            }
            catch (Exception ex)
            {
                OnError(new ErrorEventArgs(ex));
            }

            return 0;
        }

        private byte[] GetBytes<T>(T str)
        {
            var size = Marshal.SizeOf(str);
            var arr = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);

            Marshal.StructureToPtr(str, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);

            return arr;
        }


        private T FromBytes<T>(byte[] arr) where T : new()
        {
            var pinnedPacket = GCHandle.Alloc(arr, GCHandleType.Pinned);

            var obj = (T) Marshal.PtrToStructure(pinnedPacket.AddrOfPinnedObject(), typeof(T));
            pinnedPacket.Free();

            return obj;
        }


        private byte ComputeCRC(byte[] pStream, byte nLengthBytes)
        {
            byte chksum;
            byte one = 1;
            byte seven = 7;
            chksum = 0xD5;

            for (var i = 0; i < nLengthBytes; i++)
            {
                chksum = (byte) ((chksum << one) | (chksum >> seven));
                chksum += pStream[i];
            }

            return chksum;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct DEFAULT_RESPONSE_EXTENDED
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;

        [MarshalAs(UnmanagedType.Struct)] public FIDM_STATUS_EXTENDED status;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct READ_DATA_PARAMS
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct FIDM_STATUS_EXTENDED
    {
        public byte nStatusFlags;
        public byte nReadingNumber;
        public byte nError;
        public byte nThermocouple_TK0; // Tenths of Degrees Kelvin, unsigned.
        public byte nThermocouple_TK1;
        public byte nBatt_mV0; // Millivolts, unsigned.
        public byte nBatt_mV1;
        public byte nChamberOuterTemp_TK0; // Tenths of Degrees Kelvin, unsigned.
        public byte nChamberOuterTemp_TK1;
        public byte nSamplePressure_HPSI0; // Hundredths of PSI, unsigned.
        public byte nSamplePressure_HPSI1;
        public byte nAirPressure_HPSI0; // Hundredths of PSI, unsigned.
        public byte nAirPressure_HPSI1;
        public byte nH2Pressure_PSI0; // PSI, unsigned.
        public byte nH2Pressure_PSI1;
        public byte nFIDRange;
        public byte nFIDTenthsPicoA_Sat0; // Saturation, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_Sat1;
        public byte nFIDTenthsPicoA_Sat2;
        public byte nFIDTenthsPicoA_Sat3;
        public byte nFIDTenthsPicoA_In10; // Input1, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_In11;
        public byte nFIDTenthsPicoA_In12;
        public byte nFIDTenthsPicoA_In13;
        public byte nFIDTenthsPicoA_In20; // Input2, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_In21;
        public byte nFIDTenthsPicoA_In22;

        public byte nFIDTenthsPicoA_In23;

        // ---- new additions below this mark ----
        public byte nFIDTenthsPPM0;
        public byte nFIDTenthsPPM1;
        public byte nFIDTenthsPPM2;
        public byte nFIDTenthsPPM3;
        public byte nSystemCurrentMa0;
        public byte nSystemCurrentMa1;
        public byte nPumpA_power_pct;
        public byte spare;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 5)]
        public PID_LOG_ENTRY[] pid_log;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct PID_LOG_ENTRY
    {
        public ushort millisecond;
        public short derivative;
        public short p_error;
        public short err_acc;
        public short pump_pwr;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct AUTO_IGNITION_SEQUENCE
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
        public byte start_stop; // 0 = abort if running, 1 = start
        public short target_hPSI;
        public short tolerance_hPSI;
        public ushort min_temperature_rise_tK;
        public ushort max_pressure_wait_msec;
        public ushort max_ignition_wait_msec;
        public ushort sol_b_delay_msec;
        public ushort pre_purge_pump_msec;
        public ushort pre_purge_sol_A_msec;
        public ushort param1;
        public ushort param2;
        public byte use_glow_plug_b; // 0 or 1
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct FIDM_STATUS // Status
    {
        public byte nStatusFlags;
        public byte nReadingNumber;
        public byte nError;
        public byte nThermocouple_TK0; // Tenths of Degrees Kelvin, unsigned.
        public byte nThermocouple_TK1;
        public byte nBatt_mV0; // Millivolts, unsigned.
        public byte nBatt_mV1;
        public byte nChamberOuterTemp_TK0; // Tenths of Degrees Kelvin, unsigned.
        public byte nChamberOuterTemp_TK1;
        public byte nSamplePressure_HPSI0; // Hundredths of PSI, unsigned.
        public byte nSamplePressure_HPSI1;
        public byte nAirPressure_HPSI0; // Hundredths of PSI, unsigned.
        public byte nAirPressure_HPSI1;
        public byte nH2Pressure_PSI0; // PSI, unsigned.
        public byte nH2Pressure_PSI1;
        public byte nFIDRange;
        public byte nFIDTenthsPicoA_Sat0; // Saturation, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_Sat1;
        public byte nFIDTenthsPicoA_Sat2;
        public byte nFIDTenthsPicoA_Sat3;
        public byte nFIDTenthsPicoA_In10; // Input1, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_In11;
        public byte nFIDTenthsPicoA_In12;
        public byte nFIDTenthsPicoA_In13;
        public byte nFIDTenthsPicoA_In20; // Input2, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_In21;
        public byte nFIDTenthsPicoA_In22;
        public byte nFIDTenthsPicoA_In23;
    } // 28 bytes          // 31 bytes


    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct SetSamplingParams
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
        public byte nRange; // 0, 1, 2 or 3
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct DeadheadParams
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
        public byte enable; // 0 or 1
        public ushort pressure_low_limit_hPSI;
        public ushort max_duration_msec;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct PumpAux1ControlParams
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
        public byte nID;
        public byte nPowerTenthsPercent0;
        public byte nPowerTenthsPercent1;
        public byte nKickStartDurationSec;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct PumpClosedLoop
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
        public byte enable; // 0 or 1
        public short target_hPSI;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct IntegrationControlParams
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
        public byte nMode; // 0 or 1
        public byte nChargeMultiplier;
        public byte nRange;
        public byte nIntegrationTimeUs0;
        public byte nIntegrationTimeUs1;
        public byte nSamplesToAvg;
        public byte nReportMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct CalH2PressureCompensation
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
        public byte spare_for_alignment;
        public long H2_compensation_pos; // (fraction * 10^6) per LPH2 hPSI that PPM will be adjusted
        public long H2_compensation_neg; // (fraction * 10^6) per LPH2 hPSI that PPM will be adjusted
    };

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct ConfigurationReadParams
    {
        public byte nSyncCode;
        public byte nLengthBytes;
        public byte nCmdID;
    };

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public struct ConfigurationResponse
    {
        public byte nSyncCode;
        public byte nLengthBytes;

        public byte nCmdID;

        // start fidm status
        // done this way because a struct within a struct doesn't work...
        public byte nStatusFlags;
        public byte nReadingNumber;
        public byte nError;
        public byte nThermocouple_TK0; // Tenths of Degrees Kelvin, unsigned.
        public byte nThermocouple_TK1;
        public byte nBatt_mV0; // Millivolts, unsigned.
        public byte nBatt_mV1;
        public byte nChamberOuterTemp_TK0; // Tenths of Degrees Kelvin, unsigned.
        public byte nChamberOuterTemp_TK1;
        public byte nSamplePressure_HPSI0; // Hundredths of PSI, unsigned.
        public byte nSamplePressure_HPSI1;
        public byte nAirPressure_HPSI0; // Hundredths of PSI, unsigned.
        public byte nAirPressure_HPSI1;
        public byte nH2Pressure_PSI0; // PSI, unsigned.
        public byte nH2Pressure_PSI1;
        public byte nFIDRange;
        public byte nFIDTenthsPicoA_Sat0; // Saturation, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_Sat1;
        public byte nFIDTenthsPicoA_Sat2;
        public byte nFIDTenthsPicoA_Sat3;
        public byte nFIDTenthsPicoA_In10; // Input1, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_In11;
        public byte nFIDTenthsPicoA_In12;
        public byte nFIDTenthsPicoA_In13;
        public byte nFIDTenthsPicoA_In20; // Input2, Tenths of Pico Amps, signed.
        public byte nFIDTenthsPicoA_In21;
        public byte nFIDTenthsPicoA_In22;

        public byte nFIDTenthsPicoA_In23;

        // end FIDM status
        public byte nVersion;
        public byte nSectorSizeBytes0;
        public byte nSectorSizeBytes1;
        public byte nSectorSizeBytes2;
        public byte nSectorSizeBytes3;
        public byte nNumberSectors0;
        public byte nNumberSectors1;
        public byte nNumberSectors2;
        public byte nNumberSectors3;
    }; // 40 bytes
}