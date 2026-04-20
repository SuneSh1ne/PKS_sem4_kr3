using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace PKS_sem4_kr3
{
    public partial class MainWindow : Window
    {
        private HttpListener? listener;
        private CancellationTokenSource? cts;
        private bool running;
        private readonly HttpClient client = new();
        private readonly ConcurrentQueue<string> logs = new();
        private int getCount = 0;
        private int postCount = 0;
        private long totalTime = 0;
        private int totalRequests = 0;
        private readonly ConcurrentDictionary<string, string> messages = new();
        
        private readonly DispatcherTimer chartTimer;
        private PlotModel? plotModel;
        private int getRequestsThisMinute = 0;
        private int postRequestsThisMinute = 0;
        private readonly Queue<int> getHistory = new();
        private readonly Queue<int> postHistory = new();
        private int getTotal = 0;
        private int postTotal = 0;

        public MainWindow()
        {
            InitializeComponent();
            
            InitializeChart();
            
            // Инициализируем историю (10 замеров)
            for (int i = 0; i < 10; i++)
            {
                getHistory.Enqueue(0);
                postHistory.Enqueue(0);
            }
            
            chartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            chartTimer.Tick += UpdateChart;
            chartTimer.Start();
            
            txtBody.Text = "{\n  \"message\": \"Hello World!\"\n}";
        }

        private void InitializeChart()
        {
            plotModel = new PlotModel 
            { 
                Title = "Статистика запросов (за последние 50 секунд)",
                TitleColor = OxyColor.Parse("#CDD6F4"),
                TextColor = OxyColor.Parse("#CDD6F4"),
                PlotAreaBorderColor = OxyColor.Parse("#45475A")
            };
            
            var getSeries = new LineSeries
            {
                Title = "GET",
                Color = OxyColor.Parse("#A6E3A1"),
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 4
            };
            
            var postSeries = new LineSeries
            {
                Title = "POST",
                Color = OxyColor.Parse("#FAB387"),
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 4
            };
            
            plotModel.Series.Add(getSeries);
            plotModel.Series.Add(postSeries);
            
            plotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Время (замеры)",
                TitleColor = OxyColor.Parse("#6C7086"),
                TextColor = OxyColor.Parse("#CDD6F4"),
                Minimum = 0,
                Maximum = 9
            });
            
            plotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Запросов в минуту",
                TitleColor = OxyColor.Parse("#6C7086"),
                TextColor = OxyColor.Parse("#CDD6F4"),
                Minimum = 0
            });
            
            LoadChart.Model = plotModel;
        }

        private void UpdateChart(object? sender, EventArgs e)
        {
            // Добавляем текущие значения в историю
            getHistory.Dequeue();
            getHistory.Enqueue(getRequestsThisMinute);
            
            postHistory.Dequeue();
            postHistory.Enqueue(postRequestsThisMinute);
            
            // Обновляем график
            if (plotModel != null)
            {
                var getSeries = plotModel.Series[0] as LineSeries;
                var postSeries = plotModel.Series[1] as LineSeries;
                
                if (getSeries != null && postSeries != null)
                {
                    getSeries.Points.Clear();
                    postSeries.Points.Clear();
                    
                    int index = 0;
                    foreach (var value in getHistory)
                        getSeries.Points.Add(new DataPoint(index++, value));
                    
                    index = 0;
                    foreach (var value in postHistory)
                        postSeries.Points.Add(new DataPoint(index++, value));
                }
                
                plotModel.InvalidatePlot(true);
            }
            
            // Сбрасываем счетчики текущей минуты
            getRequestsThisMinute = 0;
            postRequestsThisMinute = 0;
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtPort.Text, out int port)) return;

            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                
                cts = new CancellationTokenSource();
                listener.Start();
                running = true;

                txtStatus.Text = "RUNNING";
                statusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A6E3A1")!);
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;

                AddLog($"Server started on port {port}", "#A6E3A1");
                await Task.Run(() => Listen(cts.Token));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopServer();
        }

        private void StopServer()
        {
            cts?.Cancel();
            listener?.Stop();
            listener?.Close();
            running = false;

            txtStatus.Text = "STOPPED";
            statusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8")!);
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            
            AddLog("Server stopped", "#F38BA8");
        }

        private async Task Listen(CancellationToken token)
        {
            while (running && !token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await listener!.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => Process(ctx));
                }
                catch { break; }
            }
        }

        private async Task Process(HttpListenerContext ctx)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var req = ctx.Request;
            var resp = ctx.Response;
            
            string? body = null;
            string responseText = "";
            int code = 200;
            
            if (req.HttpMethod == "GET")
            {
                Interlocked.Increment(ref getRequestsThisMinute);
                Interlocked.Increment(ref getTotal);
            }
            else if (req.HttpMethod == "POST")
            {
                Interlocked.Increment(ref postRequestsThisMinute);
                Interlocked.Increment(ref postTotal);
            }
            
            try
            {
                if (req.HasEntityBody)
                {
                    using var r = new StreamReader(req.InputStream);
                    body = await r.ReadToEndAsync();
                }
                
                AddLog($"[{req.HttpMethod}] {req.Url!.LocalPath}", "#89B4FA");

                if (req.HttpMethod == "GET")
                {
                    getCount++;
                    var data = new { time = DateTime.Now.ToString("HH:mm:ss"), total = totalRequests };
                    responseText = JsonConvert.SerializeObject(data, Formatting.Indented);
                }
                else if (req.HttpMethod == "POST" && body != null)
                {
                    postCount++;
                    try
                    {
                        var json = JObject.Parse(body);
                        var msg = json["message"]?.ToString();
                        if (msg != null)
                        {
                            var id = Guid.NewGuid().ToString();
                            messages[id] = msg;
                            responseText = JsonConvert.SerializeObject(new { id, message = msg }, Formatting.Indented);
                        }
                        else { code = 400; responseText = "{\"error\":\"message required\"}"; }
                    }
                    catch { code = 400; responseText = "{\"error\":\"invalid JSON\"}"; }
                }
                else { code = 405; responseText = "{\"error\":\"method not allowed\"}"; }
            }
            catch (Exception ex)
            {
                code = 500;
                responseText = $"{{\"error\":\"{ex.Message}\"}}";
            }
            finally
            {
                sw.Stop();
                totalRequests++;
                totalTime += sw.ElapsedMilliseconds;
                
                Dispatcher.Invoke(() =>
                {
                    txtGetCount.Text = getCount.ToString();
                    txtPostCount.Text = postCount.ToString();
                    txtAvgTime.Text = $"{(totalRequests > 0 ? totalTime / totalRequests : 0)}ms";
                    
                    // Можно добавить в заголовок графика
                    if (plotModel != null)
                        plotModel.Title = $"GET: {getTotal} | POST: {postTotal} | Запросов/мин";
                });
                
                AddLog($"[{code}] {req.HttpMethod} {req.Url!.LocalPath} - {sw.ElapsedMilliseconds}ms", code >= 200 && code < 300 ? "#A6E3A1" : "#F38BA8");
                
                var buffer = Encoding.UTF8.GetBytes(responseText);
                resp.StatusCode = code;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = buffer.Length;
                await resp.OutputStream.WriteAsync(buffer);
                resp.OutputStream.Close();
            }
        }

        private void AddLog(string msg, string color = "#CDD6F4")
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            logs.Enqueue(entry);
            
            Dispatcher.Invoke(() =>
            {
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Content = entry,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!)
                };
                lstLogs.Items.Insert(0, item);
                if (lstLogs.Items.Count > 100) lstLogs.Items.RemoveAt(100);
            });
            
            try { File.AppendAllText("logs.txt", entry + "\n"); } catch { }
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            var url = txtUrl.Text;
            var method = ((System.Windows.Controls.ComboBoxItem)cmbMethod.SelectedItem).Content.ToString() ?? "GET";
            var body = txtBody.Text;
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                // Логируем запрос
                AddLog($"CLIENT {method} {url}", "#89B4FA");
                
                HttpResponseMessage resp;
                if (method == "GET")
                    resp = await client.GetAsync(url);
                else
                    resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                
                sw.Stop();
                var text = await resp.Content.ReadAsStringAsync();
                
                // Получаем код ответа
                var statusCode = (int)resp.StatusCode;
                var statusText = resp.StatusCode.ToString();
                
                // Форматируем JSON если возможно
                try 
                { 
                    text = JToken.Parse(text).ToString(Formatting.Indented); 
                } 
                catch { }
                
                // Формируем полный ответ
                var responseBuilder = new StringBuilder();
                responseBuilder.AppendLine($"HTTP {statusCode} {statusText}");
                responseBuilder.AppendLine($"Time: {sw.ElapsedMilliseconds}ms");
                responseBuilder.AppendLine($"Server: {resp.Headers.Server?.ToString() ?? "unknown"}");
                responseBuilder.AppendLine($"Content-Type: {resp.Content.Headers.ContentType?.ToString() ?? "unknown"}");
                responseBuilder.AppendLine($"Content-Length: {resp.Content.Headers.ContentLength ?? 0} bytes");
                responseBuilder.AppendLine();
                responseBuilder.AppendLine("=== BODY ===");
                responseBuilder.AppendLine(text);
                
                txtResponse.Text = responseBuilder.ToString();
                
                // Логируем ответ с кодом и временем
                var statusColor = resp.IsSuccessStatusCode ? "#A6E3A1" : "#F38BA8";
                AddLog($"CLIENT RESPONSE [{statusCode}] {sw.ElapsedMilliseconds}ms", statusColor);
            }
            catch (Exception ex)
            {
                sw.Stop();
                
                var errorBuilder = new StringBuilder();
                errorBuilder.AppendLine($"ERROR: {ex.Message}");
                errorBuilder.AppendLine($"Time: {sw.ElapsedMilliseconds}ms");
                if (ex.InnerException != null)
                    errorBuilder.AppendLine($"Details: {ex.InnerException.Message}");
                
                txtResponse.Text = errorBuilder.ToString();
                AddLog($"CLIENT ERROR [{sw.ElapsedMilliseconds}ms]: {ex.Message}", "#F38BA8");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            chartTimer?.Stop();
            StopServer();
            client?.Dispose();
            base.OnClosed(e);
        }
    }
}