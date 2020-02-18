namespace LDARtools.PhxAccess
{
    public class Phx21Status
    {
        public double RawPpm { get; set; }
        public double Ppm { get; set; }
        public double LongAveragePpm { get; set; }
        public double ShortAveragePpm { get; set; }
        public bool UseAverage { get; set; }
        public string PpmStr { get; set; }
        public float BatteryVoltage { get; set; }
        public float ChamberOuterTemp { get; set; }
        public float SamplePressure { get; set; }
        public float AirPressure { get; set; }
        public float TankPressure { get; set; }
        public float ThermoCouple { get; set; }
        public bool IsPumpAOn { get; set; }
        public double PicoAmps { get; set; }
        public float SystemCurrent { get; set; }
        public float PumpPower { get; set; }
        public bool IsSolenoidAOn { get; set; }
        public bool IsSolenoidBOn { get; set; }
        public bool IsIgnited { get; set; }
        public byte FIDRange { get; set; }
        public string Timestamp { get; set; }
    }
}