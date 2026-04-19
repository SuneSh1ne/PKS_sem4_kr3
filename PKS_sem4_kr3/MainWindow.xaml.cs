using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PKS_sem4_kr3
{
    public partial class MainWindow : Window
    {
        private HttpListener listener;
        private CancellationTokenSource cts;
        private bool running;
        private readonly HttpClient client = new HttpClient();
        private readonly ConcurrentQueue<string> logs = new ConcurrentQueue<string>();
        private int getCount = 0;
        private int postCount = 0;
        private long totalTime = 0;
        private int totalRequests = 0;
        private readonly ConcurrentDictionary<string, string> messages = new ConcurrentDictionary<string, string>();
        private int clientRequests = 0;
        private long clientTotalTime = 0;

        public MainWindow()
        {
            InitializeComponent();
            txtBody.Text = @"{
  ""message"": ""Hello World!"",
  ""test"": true,
  ""number"": 123
}";
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtPort.Text, out int port))
            {
                MessageBox.Show("Invalid port", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                
                cts = new CancellationTokenSource();
                listener.Start();
                running = true;

                txtStatus.Text = "RUNNING";
                txtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CDD6F4"));
                statusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A6E3A1"));
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;

                AddLog($"🟢 Server started on port {port}", "#A6E3A1");
                await Task.Run(() => Listen(cts.Token));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            cts?.Cancel();
            listener?.Stop();
            listener?.Close();
            running = false;

            txtStatus.Text = "STOPPED";
            txtStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CDD6F4"));
            statusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            
            AddLog($"🔴 Server stopped", "#F38BA8");
        }

        private async Task Listen(CancellationToken token)
        {
            while (running && !token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => Process(ctx));
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task Process(HttpListenerContext ctx)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var req = ctx.Request;
            var resp = ctx.Response;
            
            string body = null;
            string responseText = "";
            int code = 200;
            
            try
            {
                if (req.HasEntityBody)
                {
                    using var r = new StreamReader(req.InputStream);
                    body = await r.ReadToEndAsync();
                }
                
                AddLog($"[{req.HttpMethod}] {req.Url.LocalPath}", "#89B4FA");

                if (req.HttpMethod == "GET")
                {
                    getCount++;
                    var data = new { 
                        time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), 
                        total = totalRequests, 
                        messages = messages.Count,
                        server = "PKS HTTP Server"
                    };
                    responseText = JsonConvert.SerializeObject(data, Formatting.Indented);
                }
                else if (req.HttpMethod == "POST")
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
                            responseText = JsonConvert.SerializeObject(new { 
                                id = id, 
                                message = msg,
                                received = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                            }, Formatting.Indented);
                        }
                        else
                        {
                            code = 400;
                            responseText = "{\"error\": \"Field 'message' is required\"}";
                        }
                    }
                    catch
                    {
                        code = 400;
                        responseText = "{\"error\": \"Invalid JSON format\"}";
                    }
                }
                else
                {
                    code = 405;
                    responseText = "{\"error\": \"Method not allowed\"}";
                }
            }
            catch (Exception ex)
            {
                code = 500;
                responseText = $"{{\"error\": \"{ex.Message}\"}}";
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
                    long avg = totalRequests > 0 ? totalTime / totalRequests : 0;
                    txtAvgTime.Text = $"{avg}ms";
                    txtLogCount.Text = logs.Count.ToString();
                });
                
                string color = code >= 200 && code < 300 ? "#A6E3A1" : "#F38BA8";
                AddLog($"[{code}] {req.HttpMethod} - {sw.ElapsedMilliseconds}ms", color);
                
                var buffer = Encoding.UTF8.GetBytes(responseText);
                resp.StatusCode = code;
                resp.ContentType = "application/json";
                resp.ContentLength64 = buffer.Length;
                await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
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
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
                };
                lstLogs.Items.Insert(0, item);
                if (lstLogs.Items.Count > 100)
                {
                    lstLogs.Items.RemoveAt(100);
                }
            });
            
            try
            {
                File.AppendAllText("logs.txt", entry + Environment.NewLine);
            }
            catch
            {
            }
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            var url = txtUrl.Text;
            var method = ((System.Windows.Controls.ComboBoxItem)cmbMethod.SelectedItem).Content.ToString();
            var body = txtBody.Text;
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                HttpResponseMessage resp;
                if (method == "GET")
                {
                    resp = await client.GetAsync(url);
                }
                else
                {
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await client.PostAsync(url, content);
                }
                
                sw.Stop();
                clientRequests++;
                clientTotalTime += sw.ElapsedMilliseconds;
                
                var text = await resp.Content.ReadAsStringAsync();
                try
                {
                    text = JToken.Parse(text).ToString(Formatting.Indented);
                }
                catch
                {
                }
                
                txtResponse.Text = text;
                
                string statusColor = resp.IsSuccessStatusCode ? "#A6E3A1" : "#F38BA8";
                txtResponseStatus.Text = $"Status: {resp.StatusCode} ({sw.ElapsedMilliseconds}ms)";
                txtResponseStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(statusColor));
                
                txtTotalRequests.Text = clientRequests.ToString();
                long avg = clientRequests > 0 ? clientTotalTime / clientRequests : 0;
                txtClientAvgTime.Text = $"{avg}ms";
                
                AddLog($"CLIENT {method} {url} -> {resp.StatusCode} ({sw.ElapsedMilliseconds}ms)", statusColor);
            }
            catch (Exception ex)
            {
                sw.Stop();
                txtResponse.Text = $"Error: {ex.Message}";
                txtResponseStatus.Text = $"Error ({sw.ElapsedMilliseconds}ms)";
                txtResponseStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
                AddLog($"CLIENT ERROR: {ex.Message}", "#F38BA8");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            btnStop_Click(null, null);
            client?.Dispose();
            base.OnClosed(e);
        }
    }
}