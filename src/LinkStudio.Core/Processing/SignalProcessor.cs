namespace LinkStudio.Core.Processing
{
    /// <summary>
    /// 波形与数据实验室内置的基础时序信号处理算子类型。
    /// </summary>
    public enum SignalFilterKind
    {
        None = 0,

        /// <summary>
        /// 因果滑动平均滤波（窗口内均值，平滑高频白噪声）。
        /// </summary>
        MovingAverage = 1,

        /// <summary>
        /// 一阶指数低通滤波（y[i] = alpha * x[i] + (1 - alpha) * y[i-1]）。
        /// </summary>
        LowPass = 2,

        /// <summary>
        /// 一阶差分 / 变化量（y[i] = x[i] - x[i-1]，突出突变与跳变点）。
        /// </summary>
        Delta = 3
    }

    /// <summary>
    /// 面向数值时序样本的轻量信号处理工具，供波形图实时叠加滤波对比与算法验证。
    /// </summary>
    public static class SignalProcessor
    {
        public static double[] Apply(
            IReadOnlyList<double> values,
            SignalFilterKind kind,
            int windowSize = 5,
            double alpha = 0.25)
        {
            ArgumentNullException.ThrowIfNull(values);
            var n = values.Count;
            if (n == 0)
            {
                return [];
            }

            var output = new double[n];
            switch (kind)
            {
                case SignalFilterKind.MovingAverage:
                    ApplyMovingAverage(values, output, Math.Clamp(windowSize, 2, 50));
                    break;
                case SignalFilterKind.LowPass:
                    ApplyLowPass(values, output, Math.Clamp(alpha, 0.02, 0.95));
                    break;
                case SignalFilterKind.Delta:
                    ApplyDelta(values, output);
                    break;
                default:
                    for (var i = 0; i < n; i++)
                    {
                        output[i] = values[i];
                    }
                    break;
            }

            return output;
        }

        private static void ApplyMovingAverage(IReadOnlyList<double> input, double[] output, int window)
        {
            var sum = 0.0;
            for (var i = 0; i < input.Count; i++)
            {
                sum += input[i];
                if (i >= window)
                {
                    sum -= input[i - window];
                    output[i] = sum / window;
                }
                else
                {
                    output[i] = sum / (i + 1);
                }
            }
        }

        private static void ApplyLowPass(IReadOnlyList<double> input, double[] output, double alpha)
        {
            output[0] = input[0];
            var oneMinusAlpha = 1.0 - alpha;
            for (var i = 1; i < input.Count; i++)
            {
                output[i] = alpha * input[i] + oneMinusAlpha * output[i - 1];
            }
        }

        private static void ApplyDelta(IReadOnlyList<double> input, double[] output)
        {
            output[0] = 0.0;
            for (var i = 1; i < input.Count; i++)
            {
                output[i] = input[i] - input[i - 1];
            }
        }
    }
}
