namespace LinkStudio.Core.Devices.Virtual
{
    /// <summary>
    /// 虚拟 MCU 自动定时发送的数据模拟模式。
    /// </summary>
    public enum VirtualMcuSimulationMode
    {
        /// <summary>
        /// 基础心跳文本（"Virtual MCU tick"）。
        /// </summary>
        BasicTick = 0,

        /// <summary>
        /// 模拟 3 轴 IMU + 温度 JSON 遥测帧（用于测试波形图与 KPI 仪表盘）。
        /// </summary>
        SimulatedImuJson = 1,

        /// <summary>
        /// 模拟多星座 GNSS NMEA-0183 语句流（含仰角、方位角、SNR 与合法异或校验）。
        /// </summary>
        SimulatedGnssNmea = 2
    }
}
