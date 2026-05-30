using System;

namespace MyBlazorApp.Models;

/// <summary>
/// Численное решение дробно-временного уравнения Блэка-Шоулза
/// с производной Римана-Лиувилля порядка alpha.
///
/// Уравнение (в переменной x = ln(S/K), обратное время tau = T - t):
///
///   D_tau^alpha V = a * V_xx + b * V_x - c * V
///
/// где a = sigma^2/2, b = r - sigma^2/2, c = r.
///
/// Начальное условие (tau=0, момент экспирации):
///   V(x, 0) = K * max(exp(x) - 1, 0)  для колл
///   V(x, 0) = K * max(1 - exp(x), 0)  для пут
///
/// Граничные условия:
///   Колл: V(x_min, tau) = 0
///         V(x_max, tau) = K*(exp(x_max) - exp(-r*tau))
///   Пут:  V(x_min, tau) = K * exp(-r*tau)
///         V(x_max, tau) = 0
/// </summary>
public class FractionalBlackScholes
{
    private readonly int    _m;           // узлов по x
    private readonly int    _n;           // шагов по времени
    private readonly double _k;           // страйк
    private readonly double _t;           // время до экспирации (лет)
    private readonly double _r;           // безрисковая ставка (доли)
    private readonly double _sigma;       // волатильность (доли)
    private readonly double _alpha;       // порядок дробной производной
    private readonly bool   _isCall;      // true=колл, false=пут

    private readonly double _xMin;        // левая граница по x
    private readonly double _xMax;        // правая граница по x
    private readonly double _h;           // шаг по x
    private readonly double _tau;         // шаг по времени
    private readonly double _invTauAlpha; // 1 / tau^alpha

    private readonly double[,] _v;        // сетка решения V[n, i]
    private readonly double[]  _omega;    // веса Грюнваля-Летникова

    // Коэффициенты уравнения
    private readonly double _a; // sigma^2 / 2
    private readonly double _b; // r - sigma^2/2
    private readonly double _c; // r

    public FractionalBlackScholes(
        double S,       // текущая цена базового актива
        double K,       // страйк
        double T,       // время до экспирации в годах
        double r,       // безрисковая ставка (доли, не %)
        double sigma,   // волатильность (доли, не %)
        double alpha,   // порядок дробной производной (0 < alpha <= 1)
        bool   isCall,  // тип опциона
        int    m = 400, // число узлов по x
        int    n = 200) // число шагов по времени
    {
        _m     = m;
        _n     = n;
        _k     = K;
        _t     = T;
        _r     = r;
        _sigma = sigma;
        _alpha = alpha;
        _isCall = isCall;

        // Коэффициенты уравнения
        _a = sigma * sigma / 2.0;
        _b = r - sigma * sigma / 2.0;
        _c = r;

        // Область по x: достаточно широкая чтобы граница не влияла
        // x = ln(S/K), поэтому при S от 0.1*K до 10*K имеем x от -2.3 до 2.3
        // берём с запасом
        double xCenter = Math.Log(S / K);
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

    /// <summary>
    /// Предвычисление весов Грюнваля-Летникова.
    /// omega[0] = 1, omega[k] = omega[k-1] * (1 - (alpha+1)/k)
    /// </summary>
    private void ComputeWeights()
    {
        _omega[0] = 1.0;
        for (int k = 1; k <= _n; k++)
            _omega[k] = _omega[k - 1] * (1.0 - (_alpha + 1.0) / k);
    }

    /// <summary>
    /// Начальное условие: функция выплат в момент экспирации (tau = 0).
    /// V(x, 0) = K * max(exp(x) - 1, 0)  для колл
    /// V(x, 0) = K * max(1 - exp(x), 0)  для пут
    /// </summary>
    private void SetInitialCondition()
    {
        for (int i = 0; i <= _m; i++)
        {
            double x  = _xMin + i * _h;
            double ex = Math.Exp(x); // S/K
            _v[0, i]  = _isCall
                ? _k * Math.Max(ex - 1.0, 0.0)
                : _k * Math.Max(1.0 - ex, 0.0);
        }
    }

    /// <summary>
    /// Граничное условие на левой границе x = xMin (S -> 0).
    /// Колл: V -> 0
    /// Пут:  V -> K * exp(-r*tau)
    /// </summary>
    private double LeftBoundary(int n)
    {
        double tau = n * _tau;
        return _isCall ? 0.0 : _k * Math.Exp(-_r * tau);
    }

    /// <summary>
    /// Граничное условие на правой границе x = xMax (S -> inf).
    /// Колл: V ~ K*(exp(xMax) - exp(-r*tau))
    /// Пут:  V -> 0
    /// </summary>
    private double RightBoundary(int n)
    {
        double tau = n * _tau;
        return _isCall
            ? _k * (Math.Exp(_xMax) - Math.Exp(-_r * tau))
            : 0.0;
    }

    /// <summary>
    /// Основной цикл решения. После вызова результат доступен через GetPrice.
    /// </summary>
    public void Solve()
    {
        double[] diagA = new double[_m + 1]; // нижняя диагональ
        double[] diagB = new double[_m + 1]; // главная диагональ
        double[] diagC = new double[_m + 1]; // верхняя диагональ
        double[] rhs   = new double[_m + 1]; // правая часть

        for (int n = 1; n <= _n; n++)
        {
            // Граничные значения на текущем шаге
            _v[n, 0]  = LeftBoundary(n);
            _v[n, _m] = RightBoundary(n);

            // Формируем систему для внутренних узлов i = 1.._m-1
            for (int i = 1; i < _m; i++)
            {
                // Коэффициенты центральных разностей
                // V_xx ~ (V[i+1] - 2V[i] + V[i-1]) / h^2
                // V_x  ~ (V[i+1] - V[i-1]) / (2h)
                double coeffA = -_a / (_h * _h) + _b / (2.0 * _h); // при V[i-1]
                double coeffB = -_invTauAlpha - 2.0 * _a / (_h * _h) - _c; // при V[i] без inv
                double coeffC = -_a / (_h * _h) - _b / (2.0 * _h); // при V[i+1]

                // Неявная схема: переносим пространственный оператор в левую часть
                // (1/tau^alpha) * V^n_i - L(V^n_i) = правая часть
                // Записываем как: -coeffA*V[i-1] + (invTauAlpha - diagB_center)*V[i] - coeffC*V[i+1] = rhs
                //
                // Уравнение: (invTauAlpha)*V_i - (a*V_xx + b*V_x - c*V) = memory_sum
                // Раскрываем:
                //   - a*(V[i+1]-2V[i]+V[i-1])/h^2
                //   - b*(V[i+1]-V[i-1])/(2h)
                //   + c*V[i]
                //   + invTauAlpha * V[i]
                //   = memory_sum

                diagA[i] = -_a / (_h * _h) + _b / (2.0 * _h);
                diagB[i] =  _invTauAlpha + 2.0 * _a / (_h * _h) + _c;
                diagC[i] = -_a / (_h * _h) - _b / (2.0 * _h);

                // Правая часть: сумма памяти (k=1..n) с весами Грюнваля-Летникова
                double memSum = 0.0;
                for (int k = 1; k <= n; k++)
                    memSum += _omega[k] * _v[n - k, i];

                rhs[i] = -_invTauAlpha * memSum;
            }

            // Коррекция правой части из-за граничных значений
            rhs[1]      -= diagA[1]      * _v[n, 0];
            rhs[_m - 1] -= diagC[_m - 1] * _v[n, _m];

            // Решаем трёхдиагональную систему методом Томаса
            ThomasAlgorithm(diagA, diagB, diagC, rhs, n);
        }
    }

    /// <summary>
    /// Алгоритм прогонки (метод Томаса) для трёхдиагональной системы.
    /// Записывает результат в _v[n, 1.._m-1].
    /// </summary>
    private void ThomasAlgorithm(double[] a, double[] b, double[] c,
                                  double[] d, int n)
    {
        int size = _m - 1; // внутренние узлы: 1.._m-1
        double[] cp = new double[_m + 1];
        double[] dp = new double[_m + 1];

        // Прямой ход
        cp[1] = c[1] / b[1];
        dp[1] = d[1] / b[1];

        for (int i = 2; i < _m; i++)
        {
            double denom = b[i] - a[i] * cp[i - 1];
            cp[i] = c[i] / denom;
            dp[i] = (d[i] - a[i] * dp[i - 1]) / denom;
        }

        // Обратный ход
        _v[n, _m - 1] = dp[_m - 1];
        for (int i = _m - 2; i >= 1; i--)
            _v[n, i] = dp[i] - cp[i] * _v[n, i + 1];
    }

    /// <summary>
    /// Возвращает цену опциона для заданной цены базового актива S.
    /// </summary>
    public double GetPrice(double S)
    {
        // Переводим S в переменную x
        double x = Math.Log(S / _k);

        // Находим ближайший узел сетки
        double posF = (x - _xMin) / _h;
        int    i0   = (int)Math.Floor(posF);

        // Ограничиваем индекс
        if (i0 < 0)  return Math.Max(_v[_n, 0], 0.0);
        if (i0 >= _m) return Math.Max(_v[_n, _m], 0.0);

        // Линейная интерполяция между соседними узлами
        double frac  = posF - i0;
        double price = _v[_n, i0] * (1.0 - frac) + _v[_n, i0 + 1] * frac;
        return Math.Max(price, 0.0);
    }
}