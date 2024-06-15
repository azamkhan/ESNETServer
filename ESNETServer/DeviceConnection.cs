using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class DeviceConnection
{
    public string IMEI { get; set; }
    public int Port { get;  set; }
    public string SourceAddress { get;  set; }
    public TcpClient Client { get; set; }
    public string State { get; set; }
    private TcpListener _listener;
    private Thread _listenerThread;
    private bool _isRunning;

    public DeviceConnection(string imei, int port, string sourceAddress, TcpClient client)
    {
        IMEI = imei;
        Port = port;
        SourceAddress = sourceAddress;
        Client = client;
        State = "Connected";
    }

    public void StartListening()
    {
        _listener = new TcpListener(System.Net.IPAddress.Any, Port);
        _listener.Start();
        _isRunning = true;
        _listenerThread = new Thread(ListenForData);
        _listenerThread.Start();
    }

    private void ListenForData()
    {
        while (_isRunning)
        {
            if (_listener.Pending())
            {
                TcpClient incomingClient = _listener.AcceptTcpClient();
                Thread clientThread = new Thread(HandleIncomingData);
                clientThread.Start(incomingClient);
            }
            Thread.Sleep(100);
        }
    }

    private void HandleIncomingData(object clientObj)
    {
        TcpClient incomingClient = (TcpClient)clientObj;
        NetworkStream incomingStream = incomingClient.GetStream();
        byte[] buffer = new byte[4096];
        int bytesRead;

        while (_isRunning)
        {
            try
            {
                bytesRead = incomingStream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    // Client disconnected
                    break;
                }

                // Forward data to the device client
                NetworkStream deviceStream = Client.GetStream();
                deviceStream.Write(buffer, 0, bytesRead);
            }
            catch
            {
                Console.WriteLine("catch-2");
                break;
            }
        }

        incomingClient.Close();
    }

    public void StopListening()
    {
        _isRunning = false;
        _listener.Stop();
        _listenerThread.Join();
    }
}
