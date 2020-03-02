using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LDARtools.PhxAccess
{
    public static class Phx42PropNames
    {
        public const string Solenoid = "Solenoid";
        public const string GlowPlug = "GlowPlug";
        public const string Current = "Current";
        public const string InternalTemp = "InternalTemp";
        public const string ExternalTemp = "ExternalTemp";
        public const string HPH2 = "HPH2";
        public const string LPH2 = "LPH2";
        public const string SamplePressure = "SamplePressure";
        public const string SamplePpl = "SamplePPL";
        public const string CombustionPpl = "CombustionPPL";
        public const string CombustionPressure = "CombustionPressure";
        public const string PicoAamps = "PicoAmps";
        public const string IsIgnited = "IsIgnited";
        public const string PPM = "PPM";
        public const string Timestamp = "Timestamp";
        public const string BatteryCharge = "BatteryCharge";
        public const string BatteryStatus = "BatteryStatus";
        public const string CaseTemp = "CaseTemp";
        public const string Vacuum = "Vacuum";
        public const string NeedleValve = "NeedleValve";
        public const string Heater = "Heater";
        public const string PaOffset = "PaOffset";
        public const string P1Target = "P1Target";
        public const string P2Target = "P2Target";
        public const string Altimeter = "Altimeter";
        public const string H2Target = "H2Target";
        public const string Volts = "Volts";
    }

    public class ReconnectNeededException : Exception
    {

    }

    public enum CommandErrorType
    {
        Shutdown,
        AutoIgnitionSequence,
        Message
    }

    public class CommandErrorEventArgs : EventArgs
    {
        public CommandErrorType ErrorType { get; }
        public string Error { get; }

        public CommandErrorEventArgs(CommandErrorType errorType, string error)
        {
            ErrorType = errorType;
            Error = error;
        }
    }




    public sealed class Phx42
    {
        private const string hostToUnit = "ZUzu";
        private const string endOfMessage = "\x0D\x0A";

        private MaxSizeList<CommMessage> receivedMessages = new MaxSizeList<CommMessage>(20);
        private ConcurrentDictionary<string, KeyValuePair<DateTime, int>> errors = new ConcurrentDictionary<string, KeyValuePair<DateTime, int>>();

        #region Define Constants

        private const int longTimeout = 5000;
        private const string msgCommCheck = "CHEK";
        private const string msgEnablePeriodicReports = "PRPT";
        private const string msgPeriodicReportinInterval = "TRPT";
        private const string msgRequestSingleReport = "SRPT";
        private const string msgReadings = "RDNG";
        private const string msgDriveLevels = "DRVL";
        private const string msgRealTimeClock = "TIME";
        private const string msgFIDReadings = "FIDR";
        private const string msgErrorReport = "EROR"; // these are sent in response to a specific command
        private const string msgSpontaneousErrorReport = "SERR"; // these are for errors that are not in response to a specific command
        private const string msgFwVersion = "VERS";
        private const string msgSystemShutdownReport = "SHUT";
        private const string msgStartAutoIgnitionSequence = "AIGS";
        private const string msgBatteryStatus = "BATS";
        private const string msgWarmupTime = "WUTM";

        #endregion Define Constants

        public event EventHandler<CommandErrorEventArgs> CommandError;
        public event EventHandler<ErrorEventArgs> Error;

        public event EventHandler<string> MessageReceived;

        private void OnError(ErrorEventArgs e)
        {
            Error?.Invoke(this, e);
        }

        public event EventHandler<DataPolledEventArgs> DataPolled;

        private readonly IInputStream _inputStream;
        private readonly IOutputStream _outputStream;
        
        private bool _isLoggingConfigured;

        public int PeriodicInterval { get; private set; } = 100;

        public Dictionary<string, string> CurrentStatus
        {
            get { return lockedStatus.ToDictionary(entry => entry.Key, entry => entry.Value); }
        }

        private ConcurrentDictionary<string, string> lockedStatus { get; } = new ConcurrentDictionary<string, string>();

        public bool IsRunning { get; set; }
        public string Status { get; set; }
        public int UseAvgPerc { get; set; }
        public int LongAverageCount { get; set; }
        public int ShortAverageCount { get; set; }
        public int AverageCutoffPpm { get; set; }

        public bool BatteryReportingEnabled { get; private set; }
        public bool ReadingsReportingEnabled { get; private set; }
        public bool DriveLevelsReportingEnabled { get; private set; }
        public bool FidReadingsReportingEnabled { get; private set; }

        public bool IsPolling => BatteryReportingEnabled | ReadingsReportingEnabled | DriveLevelsReportingEnabled | FidReadingsReportingEnabled;

        private DateTime Now => DateTime.Now;
        private DateTime UtcNow => DateTime.UtcNow;

        private readonly List<string> _messageTypes = new List<string>();

        public Phx42(IInputStream inputStream, IOutputStream outputStream)
        {
            _inputStream = inputStream;

            _outputStream = outputStream;

            UseAvgPerc = 10;
            LongAverageCount = 25;
            ShortAverageCount = 5;
            AverageCutoffPpm = 40;

            var msgFields = typeof(Phx42).GetRuntimeFields()
                .Where(f => f.IsStatic && f.IsPrivate && f.Name.StartsWith("msg"));

            foreach (var fieldInfo in msgFields)
            {
                _messageTypes.Add(fieldInfo.GetValue(null).ToString());
            }

            lockedStatus.TryAdd(Phx42PropNames.BatteryCharge, "0");
            lockedStatus.TryAdd(Phx42PropNames.BatteryStatus, "Not Working");
            lockedStatus.TryAdd(Phx42PropNames.PPM, "0");

            StartMessageHandler();

            SetTime(Now);

            EnablePeriodicReadings(msgReadings, false);
            EnablePeriodicReadings(msgDriveLevels, false);
            EnablePeriodicReadings(msgFIDReadings, false);
            EnablePeriodicReadings(msgBatteryStatus, false);

            StartHeartbeatLoop();
        }

        private bool _shutdownNow = false;
        private object _shutdownSync = new object();

        //Don't call this on the main thread since it uses Monitor.Wait()
        public void Shutdown()
        {
            _shutdownNow = true;

            lock (_shutdownSync)
            {
                while (_messageThread != null && _heartbeatThread != null)
                {
                    Monitor.Wait(_shutdownSync, 500);
                }
            }
        }

        private Task _messageThread = null;


        private void StartMessageHandler()
        {
            if (_messageThread != null && _messageThread.Status == TaskStatus.Running) return;

            _messageThread = new Task(() =>
            {
                try
                {
                    int errorcount = 0;

                    while (!_shutdownNow)
                    {
                        if (Task.CurrentId != _messageThread.Id)
                        {
                            //WriteToLog("Old message thread shutting down");
                            return;
                        }
                        try
                        {
                            var incomingMessage = ReadIncomingMessage();

                            MessageReceived?.Invoke(null, incomingMessage.RawResponse);

                            if (incomingMessage.MsgType == msgCommCheck)
                            {
                                //WriteToLog("Received CHEK message");
                            }
                            else if (incomingMessage.MsgType == msgSystemShutdownReport)
                            {
                                //WriteToLog(
                                //    $"Received {incomingMessage.MsgType} message - {incomingMessage.UnparsedString}");

                                var localMessage = incomingMessage;

                                Task.Run(() =>
                                {

                                    try
                                    {
                                        OnCommandError(CommandErrorType.Shutdown, $"Error: {localMessage.UnparsedString}");
                                    }
                                    catch (Exception ex)
                                    {
                                    //WriteToLog($"ERROR: problem with OnCommandError Shutdown: {ex.Message}");
                                    //WriteExceptionToPhxLog(ex);
                                }
                                });

                            }
                            else if (incomingMessage.MsgType == msgErrorReport || incomingMessage.MsgType == msgSpontaneousErrorReport)
                            {
                                //WriteToLog($"ERROR: {incomingMessage} - {incomingMessage.UnparsedString}");

                                string type = incomingMessage.Parameters.ContainsKey("TYPE")
                                    ? incomingMessage.Parameters["TYPE"].ToString()
                                    : null;

                                int code = incomingMessage.Parameters.ContainsKey("CODE")
                                    ? int.Parse(incomingMessage.Parameters["CODE"].ToString())
                                    : -1;

                                if (!string.IsNullOrWhiteSpace(type))
                                {
                                    errors[type] = new KeyValuePair<DateTime, int>(Now, code);
                                }

                                var localMessage = incomingMessage;

                                Task.Run(() =>
                                {
                                    try
                                    {
                                        OnCommandError(CommandErrorType.Message, $"Error: {GetErrorMessage(localMessage)}");
                                    }
                                    catch (Exception ex)
                                    {
                                    //WriteToLog($"ERROR: problem with OnCommandError: {ex.Message}");
                                    //WriteExceptionToPhxLog(ex);
                                }
                                });

                                if (type == msgStartAutoIgnitionSequence)
                                {
                                    Task.Run(() =>
                                    {
                                        try
                                        {
                                            OnCommandError(CommandErrorType.AutoIgnitionSequence, $"Error: {localMessage}");
                                        }
                                        catch (Exception ex)
                                        {
                                        //WriteToLog(
                                        //    $"ERROR: problem with OnCommandError AutoIgnitionSequence: {ex.Message}");
                                        //WriteExceptionToPhxLog(ex);
                                    }
                                    });

                                }
                            }
                            else
                            {
                                if (incomingMessage.MsgType == msgReadings || incomingMessage.MsgType == msgDriveLevels ||
                                    incomingMessage.MsgType == msgFIDReadings ||
                                    incomingMessage.MsgType == msgBatteryStatus)
                                {
                                    ParseReadings(incomingMessage);

                                    var localMessage = incomingMessage;

                                    Task.Run(() =>
                                    {
                                        if (localMessage.MsgType == msgFIDReadings ||
                                            (!FidReadingsReportingEnabled && localMessage.MsgType == msgReadings) ||
                                            (!FidReadingsReportingEnabled && !ReadingsReportingEnabled &&
                                             localMessage.MsgType == msgDriveLevels) ||
                                            (!FidReadingsReportingEnabled && !ReadingsReportingEnabled && !DriveLevelsReportingEnabled &&
                                             localMessage.MsgType == msgBatteryStatus))
                                        {
                                            try
                                            {
                                                var status = lockedStatus.ToDictionary(entry => entry.Key,
                                                    entry => entry.Value);

                                                float ppm;
                                                if (!float.TryParse(status["PPM"], out ppm))
                                                {
                                                    ppm = 0.0F;
                                                }

                                                OnDataPolled(new DataPolledEventArgs(status, ppm));
                                            }
                                            catch (Exception ex)
                                            {
                                            //WriteToLog($"ERROR: problem preparing polled data: {ex.Message}");
                                            //WriteExceptionToPhxLog(ex);
                                        }
                                        }
                                    });
                                }
                                else
                                {
                                    //WriteToLog($"Received {incomingMessage.MsgType} message");
                                }

                                receivedMessages.Add(incomingMessage);
                            }

                            Task.Delay(10).Wait(10);

                            errorcount = 0;
                        }
                        catch (Exception ex)
                        {

                            //WriteToLog($"ERROR: Message thread error #{errorcount}");

                            //WriteExceptionToPhxLog(ex);

                            errorcount++;

                            Task.Run(() => { OnError(new ErrorEventArgs(ex)); });

                            if (errorcount > 10)
                            {
                                //WriteToLog("ERROR: Message thread shutting down because of errors");

                                Task.Run(() => { OnError(new ErrorEventArgs(new ReconnectNeededException())); });

                                _shutdownNow = true;

                                return;
                            }
                        }
                    }
                }
                finally
                {
                    _messageThread = null;

                    lock (_shutdownSync)
                    {
                        Monitor.PulseAll(_shutdownSync);
                    }
                }

                //WriteToLog("Message thread shutting down");

            });

            _messageThread.Start();
            //WriteToLog("Message thread started");
        }

        private Task _heartbeatThread = null;

        private void StartHeartbeatLoop()
        {
            _heartbeatThread = Task.Run(async () =>
            {
                try
                {
                    while (!_shutdownNow)
                    {
                        try
                        {
                            await Task.Delay(900);

                            var message = new CommMessage { MsgType = msgCommCheck };
                            SendOutgoingMessage(message);
                        }
                        catch (Exception e)
                        {

                        }
                    }
                }
                finally
                {
                    _heartbeatThread = null;

                    lock (_shutdownSync)
                    {
                        Monitor.PulseAll(_shutdownSync);
                    }
                }
            });
        }

        private string GetErrorMessage(CommMessage incomingMessage)
        {
            incomingMessage.Parameters.TryGetValue("CODE", out var code);

            var codeStr = code.ToString();

            switch (codeStr)
            {
                case "5":
                    return "Too many calibration points. What are you using me for?";
                case "18":
                    return  "This application failed to set date and time. I really like to know at least when I am!" ;
                case "19":
                    return "This calibration cannot be deleted. Contact LDARtools Support.";
                case "20":
                    return "This calibration doesn't make sense. I'm reading this pA lower than the last gas you applied. Check your gases and retry calibration.";
                case "21":
                    int time = GetWarmupTime();
                    return $"No can do. I need to warm up for at least {time} seconds. Depending on your application, you may need to warm me up for longer than this requirement.";
                case "22":
                    return "I can't run on H2 this low! Feed ME!";
                case "24":
                    return "Remove probe tip filter, wait 5 seconds and reinstall same filter.";
            }

            return incomingMessage.ToString();
        }
        
        private DateTime lastParseTime;

        private void ParseReadings(CommMessage readings)
        {
            foreach (var parameter in readings.Parameters)
            {
                try
                {
                    switch (parameter.Key)
                    {
                        case "H2HP":
                            lockedStatus[Phx42PropNames.HPH2] = parameter.Value.ToString();
                            break;
                        case "H2LP":
                            lockedStatus[Phx42PropNames.LPH2] = parameter.Value.ToString();
                            break;
                        case "CHMBR":
                            lockedStatus[Phx42PropNames.InternalTemp] = parameter.Value.ToString();
                            break;
                        case "VOLTS":
                            lockedStatus[Phx42PropNames.Volts] = parameter.Value.ToString();
                            break;
                        case "P1OUT":
                            lockedStatus[Phx42PropNames.SamplePressure] = parameter.Value.ToString();
                            break;
                        case "P2OUT":
                            lockedStatus[Phx42PropNames.CombustionPressure] = parameter.Value.ToString();
                            break;
                        case "P2IN":
                            lockedStatus[Phx42PropNames.Vacuum] = parameter.Value.ToString();
                            break;
                        case "CASE":
                            lockedStatus["CaseTemp"] = parameter.Value.ToString();
                            break;
                        case "AMB":
                            lockedStatus[Phx42PropNames.ExternalTemp] = parameter.Value.ToString();
                            break;
                        case "AMPS":
                            if (readings.MsgType == msgBatteryStatus)
                            {
                                var amps = double.Parse(parameter.Value.ToString());
                                lockedStatus["BatteryStatus"] = amps > 0 ? "Charging" : "Discharging";
                            }
                            else
                            {
                                lockedStatus[Phx42PropNames.Current] =
                                    (decimal.Parse(parameter.Value.ToString())).ToString();
                            }

                            break;
                        case "NDLV":
                            lockedStatus[Phx42PropNames.NeedleValve] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "P1DRV":
                            lockedStatus[Phx42PropNames.SamplePpl] =
                                Math.Round(float.Parse(parameter.Value.ToString()), 3).ToString();
                            break;
                        case "HTR":
                            lockedStatus[Phx42PropNames.Heater] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "GPLG":
                            lockedStatus[Phx42PropNames.GlowPlug] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "SOL":
                            lockedStatus[Phx42PropNames.Solenoid] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "P2DRV":
                            lockedStatus[Phx42PropNames.CombustionPpl] =
                                Math.Round(float.Parse(parameter.Value.ToString()), 3).ToString();
                            break;
                        case "CALPPM":
                            lockedStatus["PPM"] = parameter.Value.ToString();

                            LastPpms.Add(decimal.Parse(lockedStatus["PPM"]));

                            while (LastPpms.Count > 250)
                            {
                                LastPpms.RemoveAt(0);
                            }

                            break;
                        case "PA":
                            lockedStatus[Phx42PropNames.PicoAamps] = parameter.Value.ToString();
                            break;
                        case "PAADJ":
                            lockedStatus[Phx42PropNames.PaOffset] = parameter.Value.ToString();
                            break;
                        case "PCT":
                            lockedStatus[Phx42PropNames.BatteryCharge] = parameter.Value.ToString();
                            break;
                        case "P1TGT":
                            lockedStatus[Phx42PropNames.P1Target] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "P2TGT":
                            lockedStatus[Phx42PropNames.P2Target] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "H2TGT":
                            lockedStatus[Phx42PropNames.H2Target] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "ALT":
                            lockedStatus[Phx42PropNames.Altimeter] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;

                    }
                }
                catch (Exception ex)
                {
                    //WriteExceptionToPhxLog(ex);
                    //WriteToLog($"Troublesome message {readings}");
                }
            }

            if (readings.Parameters.ContainsKey("CALPPM"))
            {
                if (lockedStatus[Phx42PropNames.PPM] == "-100.00")
                {
                    IsRunning = false;
                    lockedStatus["IsIgnited"] = bool.FalseString;
                }
                else
                {
                    IsRunning = true;
                    lockedStatus["IsIgnited"] = bool.TrueString;
                }
            }

            try
            {
                lockedStatus["Timestamp"] = UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                if (readings.MsgType == msgBatteryStatus)
                {
                    if (double.Parse(lockedStatus[Phx42PropNames.BatteryCharge]) >= 100)
                        lockedStatus["BatteryStatus"] = "Charged";
                }
            }
            catch (Exception ex)
            {
                //WriteExceptionToPhxLog(ex);
            }

            lastParseTime = Now;
        }

        private int GetWarmupTime()
        {
            CommMessage message = new CommMessage();
            message.MsgType = msgWarmupTime;

            var response = SendAndReceive(message);

            return int.Parse(response.Parameters["SEC"].ToString());
        }

        public Dictionary<string, string> GetData()
        {
            if (!IsPolling)
            {
                GetReadings();
                GetDriveLevels();
                GetFIDReadings();
            }

            return CurrentStatus;
        }

        private bool isInPollingAction = false;

        private void OnDataPolled(DataPolledEventArgs dataPolledEventArgs)
        {
            DataPolled?.Invoke(this, dataPolledEventArgs);
        }

        public void StartPollingData()
        {
            StartPollingData(200);
        }

        public void StartPollingData(int intervalInMilliseconds, bool pollBattery = true, bool pollReadings = true,
            bool pollDriveLevels = true, bool pollFIDReadings = true)
        {
            if (_isLoggingConfigured != true)
            {
                throw new Exception("Logging is not configured.  Please call ConfigureLogging before polling data.");
            }

            SetPeriodicReportingInterval(intervalInMilliseconds);
            Task.Delay(200).Wait(200);

            if (pollReadings)
            {
                EnablePeriodicReadings(msgReadings, true);
                ReadingsReportingEnabled = true;
            }

            if (pollDriveLevels)
            {
                EnablePeriodicReadings(msgDriveLevels, true);
                DriveLevelsReportingEnabled = true;
            }

            if (pollFIDReadings)
            {
                EnablePeriodicReadings(msgFIDReadings, true);
                FidReadingsReportingEnabled = true;
            }

            if (pollBattery)
            {
                EnablePeriodicReadings(msgBatteryStatus, true);
                BatteryReportingEnabled = true;
            }
        }

        public void StopPeriodicReporting(bool battery = false, bool readings = false, bool driveLevels = false,
            bool fidReadings = false)
        {
            if (readings)
            {
                EnablePeriodicReadings(msgReadings, false);
                ReadingsReportingEnabled = false;
            }

            if (driveLevels)
            {
                EnablePeriodicReadings(msgDriveLevels, false);
                DriveLevelsReportingEnabled = false;
            }

            if (fidReadings)
            {
                EnablePeriodicReadings(msgFIDReadings, false);
                FidReadingsReportingEnabled = false;
            }

            if (battery)
            {
                EnablePeriodicReadings(msgBatteryStatus, false);
                BatteryReportingEnabled = false;
            }
        }

        public void StartPeriodicReporting(bool battery = false, bool readings = false, bool driveLevels = false,
            bool fidReadings = false)
        {
            if (readings)
            {
                EnablePeriodicReadings(msgReadings, true);
                ReadingsReportingEnabled = true;
            }

            if (driveLevels)
            {
                EnablePeriodicReadings(msgDriveLevels, true);
                DriveLevelsReportingEnabled = true;
            }

            if (fidReadings)
            {
                EnablePeriodicReadings(msgFIDReadings, true);
                FidReadingsReportingEnabled = true;
            }

            if (battery)
            {
                EnablePeriodicReadings(msgBatteryStatus, true);
                BatteryReportingEnabled = true;
            }
        }

        public void StopPollingSpecificData(string dataType)
        {
            EnablePeriodicReadings(dataType, false);

            if(dataType == "BATS")
                BatteryReportingEnabled = false;
        }

        public void StopPollingData()
        {
            EnablePeriodicReadings(msgReadings, false);
            EnablePeriodicReadings(msgDriveLevels, false);
            EnablePeriodicReadings(msgFIDReadings, false);
            EnablePeriodicReadings(msgBatteryStatus, false);

            ReadingsReportingEnabled = false;
            BatteryReportingEnabled = false;
            DriveLevelsReportingEnabled = false;
            FidReadingsReportingEnabled = false;

            while (isInPollingAction)
                Task.Delay(10).Wait(10);
        }

        

        private string lastSetMessage = null;

        private string GetValueOrDefault(Dictionary<string, string> status, string key, string defaultValue = "N/A")
        {
            if (status.ContainsKey(key))
            {
                return status[key];
            }
            else
            {
                return defaultValue;
            }
        }

        private string GetLineForLog(Dictionary<string, string> status)
        {
            List<string> lines = new List<string>();


            lines.Add(GetValueOrDefault(status, Phx42PropNames.Timestamp));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.PicoAamps));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.PPM));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.PaOffset));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.SamplePressure));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.SamplePpl));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.CombustionPressure));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.CombustionPpl));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.LPH2));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.HPH2));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.Vacuum));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.InternalTemp));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.ExternalTemp));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.CaseTemp));

            lines.Add(GetValueOrDefault(status, Phx42PropNames.NeedleValve));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.Heater));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.GlowPlug));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.Solenoid));

            lines.Add(GetValueOrDefault(status, Phx42PropNames.BatteryStatus));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.BatteryCharge));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.Current));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.IsIgnited));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.P1Target));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.P2Target));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.H2Target));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.Altimeter));
            lines.Add(GetValueOrDefault(status, Phx42PropNames.Volts));

            if (!IsPolling)
            {
                lines.Add("Periodic Not Enabled");
            }
            else
            {
                List<string> parts = new List<string>();

                if (BatteryReportingEnabled) parts.Add("Battery");
                if (DriveLevelsReportingEnabled) parts.Add("Drive Levels");
                if (FidReadingsReportingEnabled) parts.Add("FID");
                if (ReadingsReportingEnabled) parts.Add("Readings");

                lines.Add($"Enabled[{PeriodicInterval}]: {string.Join(" ", parts)}");
            }

            lines.Add(lastSetMessage);

            lastSetMessage = string.Empty;

            return string.Join(",", lines);
        }

        private CommMessage SendAndReceive(CommMessage commMessage, int timeout = 2000)
        {
            try
            {
                DateTime sendTime = Now;
                SendOutgoingMessage(commMessage);


                string messageResponseType = commMessage.MsgType;

                if (commMessage.MsgType == msgRequestSingleReport)
                {
                    messageResponseType = commMessage.Parameters["TYPE"].ToString();
                }

                Stopwatch timer = Stopwatch.StartNew();

                while (timer.ElapsedMilliseconds < timeout)
                {
                    CommMessage response = null;

                    {
                        response = receivedMessages.FirstOrDefault(m => m.MsgType == messageResponseType && !m.Handled);
                        if (response != null) response.Handled = true;


                        if (response != null) return response;

                        if (errors.ContainsKey(messageResponseType))
                        {
                            if (errors[messageResponseType].Key > sendTime)
                            {
                                string message =
                                    $"ERROR: received error message for message {messageResponseType}: code {errors[messageResponseType].Value}";
                                //WriteToLog(message);
                                timer.Stop();
                                throw new Exception(message);
                            }
                        }

                        Task.Delay(20).Wait(20);
                    }
                }

                timer.Stop();
                string timeoutMessage =
                    $"ERROR: Receive timed out after {timer.Elapsed} waiting for a {messageResponseType} message";
                //WriteToLog(timeoutMessage);
                throw new Exception(timeoutMessage);
            }
            catch (Exception ex)
            {
                //WriteExceptionToPhxLog(ex);
                throw ex;
            }
        }

        private CommMessage[] SendAndReceiveMultiple(CommMessage commMessage, int responseCount, int timeout = 20000)
        {
            try
            {
                List<CommMessage> commMessages = new List<CommMessage>();

                DateTime sendTime = Now;
                SendOutgoingMessage(commMessage);

                string messageResponseType = commMessage.MsgType;

                int count = 0;

                Stopwatch timer = Stopwatch.StartNew();

                while (timer.ElapsedMilliseconds < timeout)
                {
                    CommMessage response = null;
                    
                    response = receivedMessages.FirstOrDefault(m => m.MsgType == messageResponseType && !m.Handled);

                    if (response != null) response.Handled = true;

                    if (response != null)
                    {
                        commMessages.Add(response);
                        count++;

                        timer.Restart();

                        if (count == responseCount)
                            return commMessages.ToArray();

                        if (string.IsNullOrEmpty(response.UnparsedString))
                            return commMessages.ToArray();

                        if (response.Parameters.ContainsKey("PART") && response.Parameters["PART"].ToString() == "END")
                            return commMessages.ToArray();
                    }

                    if (errors.ContainsKey(messageResponseType))
                    {
                        if (errors[messageResponseType].Key > sendTime)
                        {
                            string message =
                                $"ERROR: received error message from phx42 for message {messageResponseType}";
                            //WriteToLog(message);
                            timer.Stop();
                            throw new Exception(message);
                        }
                    }

                    Task.Delay(10).Wait(10);

                }

                timer.Stop();

                if (commMessages.Count > 0)
                    return commMessages.ToArray();

                string timeoutMessage =
                    $"ERROR: Receive timed out after {timer.Elapsed} waiting for a {messageResponseType} message";
                //WriteToLog(timeoutMessage);
                throw new Exception(timeoutMessage);
            }
            catch (Exception ex)
            {
                //WriteExceptionToPhxLog(ex);
                throw ex;
            }
        }

        private void EnablePeriodicReadings(string msgType, bool enable)
        {
            lastParseTime = Now;
            CommMessage message = new CommMessage();
            message.MsgType = msgEnablePeriodicReports;

            message.Parameters["TYPE"] = msgType;
            message.Parameters["EN"] = enable ? "1" : "0";

            if (msgType == msgReadings)
            {
                message.Parameters["TGT"] = "1";
            }
            else if (msgType == msgFIDReadings)
            {
                message.Parameters["EXT"] = "1";
            }

            SendOutgoingMessage(message);
        }

        public float GetPpm()
        {
            var fidReadings = GetFIDReadings();

            float.TryParse(fidReadings["PPM"], out var result);

            return result;
        }

        public List<decimal> LastPpms = new List<decimal>();

        public Dictionary<string, string> GetFIDReadings()
        {
            CommMessage message = new CommMessage();
            message.MsgType = msgRequestSingleReport;

            message.Parameters["TYPE"] = msgFIDReadings;
            message.Parameters["EXT"] = "1";

            var response = SendAndReceive(message);

            Dictionary<string, string> properties = new Dictionary<string, string>();

            foreach (var parameter in response.Parameters)
            {
                switch (parameter.Key)
                {
                    case "CALPPM":
                        properties["PPM"] = parameter.Value.ToString();
                        LastPpms.Add(decimal.Parse(properties["PPM"]));
                        while (LastPpms.Count > 250)
                        {
                            LastPpms.RemoveAt(0);
                        }

                        break;
                    case "PA":
                        properties[Phx42PropNames.PicoAamps] = parameter.Value.ToString();
                        break;
                    case "PAADJ":
                        properties[Phx42PropNames.PaOffset] = parameter.Value.ToString();
                        break;
                }
            }

            return properties;
        }



        public Dictionary<string, string> GetDriveLevels()
        {
            
            CommMessage message = new CommMessage();
            message.MsgType = msgRequestSingleReport;

            message.Parameters["TYPE"] = msgDriveLevels;

            var response = SendAndReceive(message, longTimeout);

            Dictionary<string, string> properties = new Dictionary<string, string>();

            foreach (var parameter in response.Parameters)
            {
                switch (parameter.Key)
                {
                    //Incomplete
                    case "P1DRV":
                        properties[Phx42PropNames.SamplePpl] =
                            Math.Round(float.Parse(parameter.Value.ToString()), 3).ToString();
                        break;
                    case "P2DRV":
                        properties[Phx42PropNames.CombustionPpl] =
                            Math.Round(float.Parse(parameter.Value.ToString()), 3).ToString();
                        break;
                    case "SOL":
                        properties[Phx42PropNames.Solenoid] = (decimal.Parse(parameter.Value.ToString())).ToString();
                        break;
                    case "NDLV":
                        properties[Phx42PropNames.NeedleValve] =
                            (decimal.Parse(parameter.Value.ToString())).ToString();
                        break;

                }
            }

            return properties;
        }

        public Dictionary<string, string> GetReadings()
        {
            
            CommMessage message = new CommMessage();
            message.MsgType = msgRequestSingleReport;

            message.Parameters["TYPE"] = msgReadings;

            message.Parameters["TGT"] = '1';

            var response = SendAndReceive(message);

            Dictionary<string, string> properties = new Dictionary<string, string>();

            foreach (var parameter in response.Parameters)
            {
                try
                {
                    switch (parameter.Key)
                    {
                        case "H2HP":
                            properties[Phx42PropNames.HPH2] = parameter.Value.ToString();
                            break;
                        case "H2LP":
                            properties[Phx42PropNames.LPH2] = parameter.Value.ToString();
                            break;
                        case "CHMBR":
                            properties[Phx42PropNames.InternalTemp] = parameter.Value.ToString();
                            break;
                        case "VOLTS":
                            properties[Phx42PropNames.Volts] = parameter.Value.ToString();
                            break;
                        case "P1OUT":
                            properties[Phx42PropNames.SamplePressure] = parameter.Value.ToString();
                            break;
                        case "P2OUT":
                            properties[Phx42PropNames.CombustionPressure] = parameter.Value.ToString();
                            break;
                        case "CASE":
                            properties["CaseTemp"] = parameter.Value.ToString();
                            break;
                        case "AMB":
                            properties[Phx42PropNames.ExternalTemp] = parameter.Value.ToString();
                            break;
                        case "AMPS":
                            properties[Phx42PropNames.Current] =
                                (decimal.Parse(parameter.Value.ToString()) * 1000).ToString();
                            break;
                        case "P1TGT":
                            properties[Phx42PropNames.P1Target] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                        case "P2TGT":
                            properties[Phx42PropNames.P2Target] =
                                (decimal.Parse(parameter.Value.ToString())).ToString();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    //WriteExceptionToPhxLog(ex);
                }
            }

            return properties;
        }

        public void SetPeriodicReportingInterval(int milliseconds)
        {
            PeriodicInterval = milliseconds;
            CommMessage message = new CommMessage();
            message.MsgType = msgPeriodicReportinInterval;

            message.Parameters["MS"] = milliseconds.ToString();

            SendOutgoingMessage(message);

            Task.Delay(500).Wait(500);
        }

        

        public string GetFirmwareVersion()
        {
            
            CommMessage message = new CommMessage();
            message.MsgType = msgFwVersion;

            var response = SendAndReceive(message);

            //WriteToLog($"Firmware Version: {response.Parameters["MAJOR"]}.{response.Parameters["MINOR"]}");

            return $"{response.Parameters["MAJOR"]}.{response.Parameters["MINOR"]}";
        }


        public void Ignite()
        {
            
            CommMessage message = new CommMessage();
            message.MsgType = msgStartAutoIgnitionSequence;

            //Set GO=1 to start, GO=0 to abort
            message.Parameters.Add("GO", 1);

            SendOutgoingMessage(message);
        }

        public void AbortIgnite()
        {
            
            CommMessage message = new CommMessage();
            message.MsgType = msgStartAutoIgnitionSequence;

            //Set GO=1 to start, GO=0 to abort
            message.Parameters.Add("GO", 0);

            SendOutgoingMessage(message);
        }

        

        

        public void SetTime(DateTime dateTime)
        {
            
            CommMessage message = new CommMessage();
            message.MsgType = msgRealTimeClock;

            message.Parameters["TS"] = dateTime.ToString("yyyy/MM/dd_HH:mm:ss", CultureInfo.InvariantCulture);

            SendOutgoingMessage(message);
        }

        public DateTime GetTime()
        {
            
            CommMessage message = new CommMessage();
            message.MsgType = msgRealTimeClock;

            var response = SendAndReceive(message, longTimeout);

            DateTime dateTime = DateTime.ParseExact(response.Parameters["TS"].ToString(), "yyyy/MM/dd_HH:mm:ss", CultureInfo.InvariantCulture);
            dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Local);

            return dateTime;
        }

        
        
        private bool SendOutgoingMessage(CommMessage msg)
        {
            string message = $"{hostToUnit} {msg.MsgType} ";

            message += string.Join(",", msg.Parameters.Select(p => $"{p.Key}={p.Value}"));

            if (!string.IsNullOrEmpty(msg.UnparsedString))
            {
                message += " ";

                message += msg.UnparsedString;
            }

            //if (msg.MsgType == msgAutoIgnitionParameters && message.EndsWith(" "))
            //    message = message.Substring(0, message.Length - 1);

            if (msg.Parameters.Count == 0 && string.IsNullOrEmpty(msg.UnparsedString) && message.EndsWith(" "))
                message = message.Substring(0, message.Length - 1);

            message += endOfMessage;

            Transmit(Encoding.UTF8.GetBytes(message));

            var type = msg.Parameters.ContainsKey("TYPE") ? msg.Parameters["TYPE"].ToString() : msg.MsgType;

            //WriteToLog($"Transmitted {type} command");

            return true;
        }

        private string _lastRawMessage = string.Empty;

        //Do not use this function! Only the message thread should use this
        private CommMessage ReadIncomingMessage(int max = 5)
        {
            string message = "";

            bool endOfIncomingMessage = false;

            List<char> readChars = new List<char>();

            try
            {
                
                while (!endOfIncomingMessage)
                {
                    
                    readChars.Add(Encoding.UTF8.GetChars(new[] { _inputStream.ReadByte() })[0]);

                    
                    try
                    {
                        if (readChars.Count > 1)
                        {
                            var last2chars = new string(readChars.Skip(readChars.Count - 2).Take(2).ToArray());

                            endOfIncomingMessage = last2chars == endOfMessage;
                        }
                    }
                    catch (Exception ex)
                    {
                        //WriteToLog(
                        //    $"Problem checking for end of message {ex.Message} - {new string(readChars.ToArray())}");
                    }
                }

                message = new string(readChars.ToArray());
                var untrimmed = message;

                var split = message.Trim().Split(' ');

                if (split.Length < 2 || split[0].Length > 5 || !_messageTypes.Contains(split[1]))
                {
                    if (max == 0) throw new Exception("Could not read a message off the socket!");

                    //WriteToLog($"Discarded message: {message}, last message is {_lastRawMessage}");

                    MessageReceived?.Invoke(null, message);

                    return ReadIncomingMessage(max - 1);
                }

                _lastRawMessage = untrimmed;

                try
                {

                    CommMessage commMessage = new CommMessage();

                    commMessage.RawResponse = untrimmed;

                    commMessage.MsgType = split[1];

                    if (split.Length > 2)
                    {
                        if (split[2].Contains("="))
                        {
                            var paramSplit = split[2].Split(',');

                            foreach (var param in paramSplit)
                            {

                                //Sometimes the data is corrupt.  Not sure what to do except salvage what we can.  Perhaps try again?
                                try
                                {
                                    var keyValue = param.Split('=');

                                    commMessage.Parameters[keyValue[0]] = keyValue[1];
                                }
                                catch (Exception ex)
                                {
                                    //WriteToLog($"Could not parse [{param}]");
                                    //WriteExceptionToPhxLog(ex);
                                }
                            }
                        }
                        else
                        {
                            commMessage.UnparsedString = string.Join(" ", split.Skip(2));
                        }
                    }

                    if (string.IsNullOrWhiteSpace(commMessage.UnparsedString))
                    {
                        commMessage.UnparsedString = string.Join(" ", split.Skip(3));
                    }

                    return commMessage;
                }
                catch (Exception ex)
                {
                    //WriteToLog(
                    //    $"ERROR: Trouble parsing message [{message}], last message is {_lastRawMessage}, last trace line {messageThreadLastLine} exception follows");
                    //WriteExceptionToPhxLog(ex);
                    throw ex;
                }
            }
            catch (Exception ex)
            {
                //WriteToLog(
                //    $"ERROR: Trouble parsing message [{message}], last message is {_lastRawMessage}, last trace line {messageThreadLastLine} exception follows");
                //WriteExceptionToPhxLog(ex);
                throw ex;
            }
        }

        private bool Transmit(byte[] buf)
        {
            lock (_outputStream)
            {
                _outputStream.Write(buf, 0, buf.Length);

                try
                {
                    _outputStream.Flush();
                }
                catch (Exception)
                {
                    //this is ok, some underlying streams don't implement flush and throw an exception
                }
            }

            return true;
        }


        public class CommMessage
        {
            public string MsgType;
            public Dictionary<string, object> Parameters = new Dictionary<string, object>();
            public string UnparsedString;
            public string RawResponse;

            public override string ToString()
            {
                return MsgType + "  " + String.Join(",", Parameters.Select(kv => $"{kv.Key}={kv.Value}")) + " " +
                       UnparsedString;
            }

            public bool Handled { get; set; }
        }

        private void OnCommandError(CommandErrorType errorType, string error)
        {
            CommandError?.Invoke(this, new CommandErrorEventArgs(errorType, error));
        }
    }
}