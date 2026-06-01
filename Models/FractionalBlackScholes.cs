using System;

namespace FractionalBlackScholes.Models
{
    /// <summary>
    /// Численное решение дробно-временного уравнения Блэка-Шоулза
    /// с производной Римана–Лиувилля порядка alpha.
    ///
    /// Уравнение (переменная x = ln(S/K), обратное время tau = T - t):
    ///   D_tau^alpha V = a·V_xx + b·V_x − c·V
    ///   a = σ²/2,  b = r − σ²/2,  c = r
    ///
    /// Дискретизация по времени: схема Грюнваля–Летникова.
    /// Дискретизация по пространству: неявная разностная схема (метод Томаса).
    /// Параметры сетки m=400, n=200 совпадают с оригинальным WPF-приложением.
    /// </summary>
    public class FractionalBlackScholes
    {
        private readonly int    _m;
        private readonly int    _n;
        private readonly double _k;
        private readonly double _t;
        private readonly double _r;
        private readonly double _sigma;
        private readonly double _alpha;
        private readonly bool   _isCall;

        private readonly double _xMin;
        private readonly double _xMax;
        private readonly double _h;
        private readonly double _tau;
        private readonly double _invTauAlpha;

        private readonly double[,] _v;
        private readonly double[]  _omega;

        private readonly double _a; // σ² / 2
        private readonly double _b; // r − σ²/2
        private readonly double _c; // r

        /// <summary>
        /// Порядок параметров ТОЧНО совпадает с оригинальным WPF-классом:
        /// S, K, T, r, sigma, alpha, isCall, m, n.
        /// </summary>
        public FractionalBlackScholes(
            double S,
            double K,
            double T,
            double r,       // ← r идёт ПЕРЕД sigma (как в оригинале)
            double sigma,   // ← sigma идёт ПОСЛЕ r
            double alpha,
            bool   isCall,
            int    m = 400, // совпадает с оригиналом
            int    n = 200) // совпадает с оригиналом
        {
            _m      = m;
            _n      = n;
            _k      = K;
            _t      = T;
            _r      = r;
            _sigma  = sigma;
            _alpha  = alpha;
            _isCall = isCall;

            _a = sigma * sigma / 2.0;
            _b = r - sigma * sigma / 2.0;
            _c = r;

            double xCenter   = Math.Log(S / K);
            double halfWidth = Math.Max(4.0, 4.0 * sigma * Math.Sqrt(T));
            _xMin = xCenter - halfWidth;
            _xMax = xCenter + halfWidth;

            _h           = (_xMax - _xMin) / m;
            _tau         = T / n;
            _invTauAlpha = 1.0 / Math.Pow(_tau, alpha);

            _v     = new double[n + 1, m + 1];
            _omega = new double[n + 1];

            ComputeWeights();
            SetInitialCondition();
        }

        private void ComputeWeights()
        {
            _omega[0] = 1.0;
            for (int k = 1; k <= _n; k++)
                _omega[k] = _omega[k - 1] * (1.0 - (_alpha + 1.0) / k);
        }

        private void SetInitialCondition()
        {
            for (int i = 0; i <= _m; i++)
            {
                double x  = _xMin + i * _h;
                double ex = Math.Exp(x);
                _v[0, i]  = _isCall
                    ? _k * Math.Max(ex - 1.0, 0.0)
                    : _k * Math.Max(1.0 - ex, 0.0);
            }
        }

        private double LeftBoundary(int step)
        {
            double tau = step * _tau;
            return _isCall ? 0.0 : _k * Math.Exp(-_r * tau);
        }

        private double RightBoundary(int step)
        {
            double tau = step * _tau;
            return _isCall
                ? _k * (Math.Exp(_xMax) - Math.Exp(-_r * tau))
                : 0.0;
        }

        public void Solve()
        {
            double[] diagA = new double[_m + 1];
            double[] diagB = new double[_m + 1];
            double[] diagC = new double[_m + 1];
            double[] rhs   = new double[_m + 1];

            for (int n = 1; n <= _n; n++)
            {
                _v[n, 0]  = LeftBoundary(n);
                _v[n, _m] = RightBoundary(n);

                for (int i = 1; i < _m; i++)
                {
                    diagA[i] = -_a / (_h * _h) + _b / (2.0 * _h);
                    diagB[i] =  _invTauAlpha + 2.0 * _a / (_h * _h) + _c;
                    diagC[i] = -_a / (_h * _h) - _b / (2.0 * _h);

                    double memSum = 0.0;
                    for (int k = 1; k <= n; k++)
                        memSum += _omega[k] * _v[n - k, i];

                    rhs[i] = -_invTauAlpha * memSum;
                }

                rhs[1]      -= diagA[1]      * _v[n, 0];
                rhs[_m - 1] -= diagC[_m - 1] * _v[n, _m];

                ThomasAlgorithm(diagA, diagB, diagC, rhs, n);
            }
        }

        private void ThomasAlgorithm(double[] a, double[] b, double[] c, double[] d, int n)
        {
            double[] cp = new double[_m + 1];
            double[] dp = new double[_m + 1];

            cp[1] = c[1] / b[1];
            dp[1] = d[1] / b[1];

            for (int i = 2; i < _m; i++)
            {
                double denom = b[i] - a[i] * cp[i - 1];
                cp[i] = c[i] / denom;
                dp[i] = (d[i] - a[i] * dp[i - 1]) / denom;
            }

            _v[n, _m - 1] = dp[_m - 1];
            for (int i = _m - 2; i >= 1; i--)
                _v[n, i] = dp[i] - cp[i] * _v[n, i + 1];
        }

        public double GetPrice(double S)
        {
            double x    = Math.Log(S / _k);
            double posF = (x - _xMin) / _h;
            int    i0   = (int)Math.Floor(posF);

            if (i0 < 0)   return Math.Max(_v[_n, 0], 0.0);
            if (i0 >= _m) return Math.Max(_v[_n, _m], 0.0);

            double frac  = posF - i0;
            double price = _v[_n, i0] * (1.0 - frac) + _v[_n, i0 + 1] * frac;
            return Math.Max(price, 0.0);
        }
    }

    /// <summary>
    /// Фасад для DI. Принимает параметры в порядке (S, K, T, sigma, r, alpha)
    /// — как того ожидает UI — и передаёт в конструктор FractionalBlackScholes
    /// в правильном порядке (S, K, T, r, sigma, alpha).
    ///
    /// sigma и r передавались в конструктор в обратном порядке,
    /// из-за чего модель считала с чужими коэффициентами и давала другой результат.
    /// </summary>
    public class FractionalBlackScholesEngine
    {
        /// <summary>Цена колл-опциона. Параметры: S, K, T, sigma, r, alpha.</summary>
        public double CalculateCallPrice(
            double S, double K, double T,
            double sigma, double r, double alpha)  // UI-порядок: sigma перед r
        {
            // Конструктор ожидает: S, K, T, r, sigma, alpha  ← явные имена исключают путаницу
            var solver = new FractionalBlackScholes(
                S: S, K: K, T: T,
                r: r,         // ← r на своём месте
                sigma: sigma, // ← sigma на своём месте
                alpha: alpha,
                isCall: true);
            solver.Solve();
            return solver.GetPrice(S);
        }

        /// <summary>Цена пут-опциона. Параметры: S, K, T, sigma, r, alpha.</summary>
        public double CalculatePutPrice(
            double S, double K, double T,
            double sigma, double r, double alpha)
        {
            var solver = new FractionalBlackScholes(
                S: S, K: K, T: T,
                r: r,
                sigma: sigma,
                alpha: alpha,
                isCall: false);
            solver.Solve();
            return solver.GetPrice(S);
        }
    }
}
