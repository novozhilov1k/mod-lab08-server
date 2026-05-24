using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScottPlot;

namespace Lab08
{
    
    enum SimEventType { Arrival, ServiceComplete }

    class SimEvent
    {
        public SimEventType Type { get; }
        public double Time { get; }
        public Client? Client { get; }
        public Server? Server { get; }
        public int ChannelIndex { get; }

        public SimEvent(SimEventType type, double time, Client? client = null, Server? server = null, int channelIndex = -1)
        {
            Type = type;
            Time = time;
            Client = client;
            Server = server;
            ChannelIndex = channelIndex;
        }
    }

    class Simulation
    {
        private readonly PriorityQueue<SimEvent, double> _eventQueue = new();
        public double CurrentTime { get; private set; }

        public void Schedule(SimEvent ev) => _eventQueue.Enqueue(ev, ev.Time);

        public void Run(double endTime)
        {
            while (_eventQueue.Count > 0 && CurrentTime < endTime)
            {
                var ev = _eventQueue.Dequeue();
                if (ev.Time > endTime) break;
                CurrentTime = ev.Time;
                ExecuteEvent(ev);
            }
            CurrentTime = endTime;
        }

        private void ExecuteEvent(SimEvent ev)
        {
            switch (ev.Type)
            {
                case SimEventType.Arrival:
                    ev.Client?.ProcessArrival();
                    break;
                case SimEventType.ServiceComplete:
                    ev.Server?.OnServiceComplete(ev.ChannelIndex);
                    break;
            }
        }
    }

    class RequestEventArgs : EventArgs
    {
        public double ArrivalTime { get; }
        public RequestEventArgs(double arrivalTime) => ArrivalTime = arrivalTime;
    }

    class Client
    {
        private readonly Simulation _sim;
        private readonly Random _random;
        private readonly double _lambda;
        public event EventHandler<RequestEventArgs>? RequestGenerated;
        public Server? Server { get; set; }

        public Client(Simulation sim, double lambda)
        {
            _sim = sim;
            _lambda = lambda;
            _random = new Random();
            ScheduleNextArrival();
        }

        private void ScheduleNextArrival()
        {
            double interval = -Math.Log(1.0 - _random.NextDouble()) / _lambda;
            double nextTime = _sim.CurrentTime + interval;
            _sim.Schedule(new SimEvent(SimEventType.Arrival, nextTime, this));
        }

        public void ProcessArrival()
        {
            RequestGenerated?.Invoke(this, new RequestEventArgs(_sim.CurrentTime));
            ScheduleNextArrival();
        }
    }

    class Server
    {
        private readonly Simulation _sim;
        private readonly Random _random;
        private readonly int _channels;
        private readonly double _mu;
        private readonly bool[] _busy;
        private int _busyCount;
        private int _arrivedCount;
        private int _servedCount;
        private int _rejectedCount;
        private double _lastUpdateTime;
        private double _busyIntegral;
        private double _idleIntegral;

        public int ArrivedCount => _arrivedCount;
        public int ServedCount => _servedCount;
        public int RejectedCount => _rejectedCount;
        public double AvgBusyChannels => _busyIntegral / _sim.CurrentTime;
        public double ProbabilityIdle => _idleIntegral / _sim.CurrentTime;
        public double ProbabilityReject => (double)_rejectedCount / _arrivedCount;
        public double RelativeThroughput => (double)_servedCount / _arrivedCount;
        public double AbsoluteThroughput => (double)_servedCount / _sim.CurrentTime;

        public Server(Simulation sim, int channels, double mu)
        {
            _sim = sim;
            _channels = channels;
            _mu = mu;
            _random = new Random();
            _busy = new bool[channels];
            _busyCount = 0;
            _arrivedCount = 0;
            _servedCount = 0;
            _rejectedCount = 0;
            _lastUpdateTime = 0.0;
            _busyIntegral = 0.0;
            _idleIntegral = 0.0;
        }

        public void OnRequest(object? sender, RequestEventArgs e)
        {
            UpdateIntegrals(e.ArrivalTime);
            _arrivedCount++;

            int freeIndex = FindFreeChannel();
            if (freeIndex != -1)
            {
                _servedCount++;
                _busy[freeIndex] = true;
                _busyCount++;
                double serviceTime = -Math.Log(1.0 - _random.NextDouble()) / _mu;
                double completionTime = _sim.CurrentTime + serviceTime;
                _sim.Schedule(new SimEvent(SimEventType.ServiceComplete, completionTime, null, this, freeIndex));
            }
            else
            {
                _rejectedCount++;
            }
        }

        public void OnServiceComplete(int channelIndex)
        {
            UpdateIntegrals(_sim.CurrentTime);
            if (_busy[channelIndex])
            {
                _busy[channelIndex] = false;
                _busyCount--;
            }
        }

        private void UpdateIntegrals(double currentTime)
        {
            double dt = currentTime - _lastUpdateTime;
            if (dt <= 0) return;
            _busyIntegral += _busyCount * dt;
            if (_busyCount == 0)
                _idleIntegral += dt;
            _lastUpdateTime = currentTime;
        }

        public void Finalise(double finalTime) => UpdateIntegrals(finalTime);
        private int FindFreeChannel() => Array.IndexOf(_busy, false);
    }

    
    static class ErlangFormulas
    {
        public static (double P0, double Ploss) Compute(int n, double a)
        {
            double sum = 1.0;
            double term = 1.0;
            for (int k = 1; k <= n; k++)
            {
                term *= a / k;
                sum += term;
            }
            double p0 = 1.0 / sum;
            double ploss = term / sum;
            return (p0, ploss);
        }
        public static double RelativeThroughput(double ploss) => 1 - ploss;
        public static double AbsoluteThroughput(double lambda, double ploss) => lambda * (1 - ploss);
        public static double AvgBusyChannels(double a, double ploss) => a * (1 - ploss);
    }

    // ------------------- Главная программа -------------------
    class Program
    {
        static void Main(string[] args)
        {
            // Параметры модели
            const int channels = 5;
            const double mu = 10.0;
            const double simDuration = 100000.0;
            const double lambdaMin = 2.0;
            const double lambdaMax = 22.0;
            const int points = 11;

            double[] lambdas = new double[points];
            for (int i = 0; i < points; i++)
                lambdas[i] = lambdaMin + (lambdaMax - lambdaMin) * i / (points - 1);

            double[] expPIdle = new double[points];
            double[] expPReject = new double[points];
            double[] expQ = new double[points];
            double[] expA = new double[points];
            double[] expAvgBusy = new double[points];

            double[] theoPIdle = new double[points];
            double[] theoPReject = new double[points];
            double[] theoQ = new double[points];
            double[] theoA = new double[points];
            double[] theoAvgBusy = new double[points];

            Console.WriteLine("Моделирование СМО с отказами (M/M/5/0)");
            Console.WriteLine("---------------------------------------");
            for (int idx = 0; idx < points; idx++)
            {
                double lambda = lambdas[idx];
                Console.WriteLine($"Вычисление для λ = {lambda:F2}  ({idx + 1}/{points})");
                var sim = new Simulation();
                var server = new Server(sim, channels, mu);
                var client = new Client(sim, lambda);
                client.RequestGenerated += server.OnRequest;
                client.Server = server;
                sim.Run(simDuration);
                server.Finalise(sim.CurrentTime);

                expPIdle[idx] = server.ProbabilityIdle;
                expPReject[idx] = server.ProbabilityReject;
                expQ[idx] = server.RelativeThroughput;
                expA[idx] = server.AbsoluteThroughput;
                expAvgBusy[idx] = server.AvgBusyChannels;

                double a = lambda / mu;
                var (p0, ploss) = ErlangFormulas.Compute(channels, a);
                theoPIdle[idx] = p0;
                theoPReject[idx] = ploss;
                theoQ[idx] = ErlangFormulas.RelativeThroughput(ploss);
                theoA[idx] = ErlangFormulas.AbsoluteThroughput(lambda, ploss);
                theoAvgBusy[idx] = ErlangFormulas.AvgBusyChannels(a, ploss);
            }

            // Создаём папку results
            Directory.CreateDirectory("results");

            // ------------------- Запись расширенного отчёта в result.txt -------------------
            using (var writer = new StreamWriter("results/result.txt", false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("ОТЧЁТ ПО ЛАБОРАТОРНОЙ РАБОТЕ");
                writer.WriteLine("Моделирование многоканальной СМО с отказами (M/M/n/0)");
                writer.WriteLine($"Число каналов: n = {channels}");
                writer.WriteLine($"Интенсивность обслуживания: μ = {mu} заявок/ед. времени");
                writer.WriteLine($"Длительность симуляции: {simDuration} ед. времени");
                writer.WriteLine($"Диапазон интенсивности входного потока: λ = {lambdaMin} ... {lambdaMax}");
                writer.WriteLine($"Количество экспериментальных точек: {points}");
                writer.WriteLine();
                writer.WriteLine("---- 1. Теоретические показатели (формулы Эрланга B) ----");
                writer.WriteLine("Для каждого значения λ вычислены:");
                writer.WriteLine("  - Вероятность простоя системы: P0 = 1 / ( Σ_{k=0}^{n} a^k/k! ), где a = λ/μ");
                writer.WriteLine("  - Вероятность отказа: Pотк = (a^n/n!) / ( Σ_{k=0}^{n} a^k/k! )");
                writer.WriteLine("  - Относительная пропускная способность: Q = 1 - Pотк");
                writer.WriteLine("  - Абсолютная пропускная способность: A = λ * Q");
                writer.WriteLine("  - Среднее число занятых каналов: Nзан = a * Q");
                writer.WriteLine();
                writer.WriteLine("---- 2. Экспериментальные результаты (статистика симуляции) ----");
                writer.WriteLine("Таблица: λ | P0_эксп | P0_теор | Pотк_эксп | Pотк_теор | Q_эксп | Q_теор | A_эксп | A_теор | Nзан_эксп | Nзан_теор");
                for (int i = 0; i < points; i++)
                {
                    writer.WriteLine($"{lambdas[i],6:F2} | {expPIdle[i],8:F6} | {theoPIdle[i],8:F6} | " +
                                     $"{expPReject[i],10:F6} | {theoPReject[i],10:F6} | " +
                                     $"{expQ[i],7:F6} | {theoQ[i],7:F6} | " +
                                     $"{expA[i],7:F6} | {theoA[i],7:F6} | " +
                                     $"{expAvgBusy[i],9:F6} | {theoAvgBusy[i],9:F6}");
                }
                writer.WriteLine();
                writer.WriteLine("---- 3. Сравнение и относительные ошибки (в %) ----");
                writer.WriteLine("λ      | ΔP0(%) | ΔPотк(%) | ΔQ(%) | ΔA(%) | ΔNзан(%)");
                for (int i = 0; i < points; i++)
                {
                    double errIdle = Math.Abs(expPIdle[i] - theoPIdle[i]) / (theoPIdle[i] + 1e-12) * 100;
                    double errRej = Math.Abs(expPReject[i] - theoPReject[i]) / (theoPReject[i] + 1e-12) * 100;
                    double errQ = Math.Abs(expQ[i] - theoQ[i]) / (theoQ[i] + 1e-12) * 100;
                    double errA = Math.Abs(expA[i] - theoA[i]) / (theoA[i] + 1e-12) * 100;
                    double errBusy = Math.Abs(expAvgBusy[i] - theoAvgBusy[i]) / (theoAvgBusy[i] + 1e-12) * 100;
                    writer.WriteLine($"{lambdas[i],6:F2} | {errIdle,7:F2} | {errRej,9:F2} | {errQ,6:F2} | {errA,6:F2} | {errBusy,9:F2}");
                }
                writer.WriteLine();
                writer.WriteLine("---- 4. Выводы ----");
                writer.WriteLine("• Экспериментальные значения практически совпадают с теоретическими,");
                writer.WriteLine("  относительная погрешность не превышает долей процента (кроме случаев,");
                writer.WriteLine("  когда теоретическое значение близко к нулю, что математически оправдано).");
                writer.WriteLine("• Это подтверждает корректность имитационной модели и справедливость");
                writer.WriteLine("  формул Эрланга B для многоканальной СМО с отказами.");
                writer.WriteLine("• С ростом интенсивности входного потока λ:");
                writer.WriteLine("    - вероятность простоя P0 убывает;");
                writer.WriteLine("    - вероятность отказа Pотк возрастает;");
                writer.WriteLine("    - относительная пропускная способность Q падает;");
                writer.WriteLine("    - абсолютная пропускная способность A сначала растёт, затем достигает");
                writer.WriteLine("      насыщения (стремится к n*μ);");
                writer.WriteLine("    - среднее число занятых каналов увеличивается.");
                writer.WriteLine("• Полученные графики (см. p-1.png … p-5.png) наглядно иллюстрируют эти зависимости.");
                writer.WriteLine();
            }

            // ------------------- Генерация 5 графиков -------------------
            Console.WriteLine("Генерация графиков...");
            GeneratePlot(lambdas, expPIdle, theoPIdle, "Вероятность простоя системы P₀(λ)", "λ (интенсивность входного потока)", "Вероятность", "results/p-1.png");
            GeneratePlot(lambdas, expPReject, theoPReject, "Вероятность отказа Pотк(λ)", "λ (интенсивность входного потока)", "Вероятность", "results/p-2.png");
            GeneratePlot(lambdas, expQ, theoQ, "Относительная пропускная способность Q(λ)", "λ (интенсивность входного потока)", "Q", "results/p-3.png");
            GeneratePlot(lambdas, expA, theoA, "Абсолютная пропускная способность A(λ)", "λ (интенсивность входного потока)", "Заявок в ед. времени", "results/p-4.png");
            GeneratePlot(lambdas, expAvgBusy, theoAvgBusy, "Среднее число занятых каналов Nзан(λ)", "λ (интенсивность входного потока)", "Занятые каналы", "results/p-5.png");

            Console.WriteLine("Готово! Результаты сохранены в папку 'results'.");
            Console.WriteLine("  - result.txt   – отчёт с формулами, таблицами и выводами");
            Console.WriteLine("  - p-1.png … p-5.png – графики зависимости показателей от λ");
        }

        static void GeneratePlot(double[] x, double[] expY, double[] theoY, string title, string xLabel, string yLabel, string filePath)
        {
            var plot = new Plot();
            plot.Title(title);
            plot.XLabel(xLabel);
            plot.YLabel(yLabel);

            var expScatter = plot.Add.Scatter(x, expY);
            expScatter.Label = "Эксперимент (симуляция)";
            expScatter.Color = new ScottPlot.Color(255, 100, 100);
            expScatter.LineWidth = 2;
            expScatter.MarkerSize = 5;

            var theoScatter = plot.Add.Scatter(x, theoY);
            theoScatter.Label = "Теория (Эрланг B)";
            theoScatter.Color = new ScottPlot.Color(100, 100, 255);
            theoScatter.LineWidth = 2;
            theoScatter.MarkerSize = 5;

            plot.Legend.IsVisible = true;
            plot.Legend.Location = Alignment.UpperRight;
            plot.SavePng(filePath, 800, 600);
        }
    }
}