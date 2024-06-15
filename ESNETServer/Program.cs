using System.IO.Pipes;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class TcpServer
{
    private TcpListener _listener;
    private Thread _listenerThread;
    private bool _isRunning;
    private Dictionary<string, DeviceConnection> _registeredDevices;
    private int _nextPort;
    private string _connectionString = "Data Source=clients.db;Version=3;";

    public TcpServer()
    {
        _registeredDevices = new Dictionary<string, DeviceConnection>();
        _nextPort = 6000; // Starting port for device assignments
        InitializeDatabase();
        LoadRegisteredDevices();
    }

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _isRunning = true;
        _listenerThread = new Thread(ListenForClients);
        _listenerThread.Start();
        Console.WriteLine($"Server started on port {port}");
    }

    private void InitializeDatabase()
    {
        using (var connection = new SQLiteConnection(_connectionString))
        {
            connection.Open();
            string tableCreationQuery = @"CREATE TABLE IF NOT EXISTS Devices (
                IMEI TEXT PRIMARY KEY,
                Port INTEGER,
                SourceAddress TEXT,
                State TEXT,
                RegistrationDateTime TEXT,
                LastConnectedDateTime TEXT
            )";
            SQLiteCommand command = new SQLiteCommand(tableCreationQuery, connection);
            command.ExecuteNonQuery();
        }
    }

    private void LoadRegisteredDevices()
    {
        using (var connection = new SQLiteConnection(_connectionString))
        {
            connection.Open();
            string query = "SELECT IMEI, Port, SourceAddress, State, RegistrationDateTime, LastConnectedDateTime FROM Devices";
            SQLiteCommand command = new SQLiteCommand(query, connection);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    string imei = reader.GetString(0);
                    int port = reader.GetInt32(1);
                    string sourceAddress = reader.GetString(2);
                    string state = reader.GetString(3);
                    _registeredDevices[imei] = new DeviceConnection(imei, port, sourceAddress, null);
                    _nextPort = Math.Max(_nextPort, port + 1);
                }
            }
        }
    }

    private void ListenForClients()
    {
        while (_isRunning)
        {
            if (_listener.Pending())
            {
                TcpClient client = _listener.AcceptTcpClient();
                Thread clientThread = new Thread(HandleClientComm);
                clientThread.Start(client);
            }
            Thread.Sleep(100);
        }
    }

    private void HandleClientComm(object clientObj)
    {
        TcpClient tcpClient = (TcpClient)clientObj;
        NetworkStream clientStream = tcpClient.GetStream();
        byte[] message = new byte[4096];
        int bytesRead;

        while (_isRunning)
        {
            try
            {
                bytesRead = clientStream.Read(message, 0, 4096);
                if (bytesRead == 0)
                {
                    // Client disconnected
                    break;
                }

                string receivedMessage = Encoding.ASCII.GetString(message, 0, bytesRead);
                Console.WriteLine($"Received: {receivedMessage}");

                string[] parts = receivedMessage.Split(';');
                if (parts.Length == 2 && parts[0] == "INF")
                {
                    string imei = parts[1];
                    string clientIp = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString();
                    int clientPort = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Port;
                    string sourceAddress = $"{clientIp}:{clientPort}";

                    if (!_registeredDevices.ContainsKey(imei))
                    {
                        int port = _nextPort++;
                        DeviceConnection deviceConnection = new DeviceConnection(imei, port, sourceAddress, tcpClient);
                        _registeredDevices[imei] = deviceConnection;
                        deviceConnection.StartListening();
                        DateTime registrationDateTime = DateTime.Now;
                        DateTime lastConnectedDateTime = registrationDateTime;
                        SaveDeviceInfo(imei, port, sourceAddress, "Connected", registrationDateTime, lastConnectedDateTime);
                    }
                    else
                    {
                        var deviceConnection = _registeredDevices[imei];
                        deviceConnection.Client = tcpClient;
                        deviceConnection.State = "Connected";
                        deviceConnection.SourceAddress = sourceAddress;
                        UpdateDeviceStateAndLastConnected(imei, "Connected", sourceAddress, DateTime.Now);
                    }
                    Console.WriteLine($"Device {imei} registered with port {_registeredDevices[imei].Port} and source address {_registeredDevices[imei].SourceAddress}");
                }
                else if (parts.Length == 2 && parts[0] == "HEARTBEAT")
                {
                    string imei = parts[1];
                    if (_registeredDevices.ContainsKey(imei))
                    {
                        string clientIp = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString();
                        int clientPort = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Port;
                        string sourceAddress = $"{clientIp}:{clientPort}";
                        UpdateClientInfo(imei, sourceAddress);
                        UpdateDeviceLastConnected(imei, DateTime.Now);

                        byte[] response = Encoding.ASCII.GetBytes("HEARTBEAT_ACK");
                        clientStream.Write(response, 0, response.Length);
                    }
                }
            }
            catch
            {
                // An error occurred, client disconnected
                Console.WriteLine("catch-1");
                break;
            }
        }

        // Handle client disconnection
        HandleClientDisconnection(tcpClient);

        tcpClient.Close();
    }

    private void HandleClientDisconnection(TcpClient tcpClient)
    {
        string disconnectedImei = null;
        foreach (var entry in _registeredDevices)
        {
            if (entry.Value.Client == tcpClient)
            {
                disconnectedImei = entry.Key;
                break;
            }
        }

        if (disconnectedImei != null)
        {
            var deviceConnection = _registeredDevices[disconnectedImei];
            deviceConnection.Client = null;
            deviceConnection.State = "Disconnected";
            UpdateDeviceState(disconnectedImei, "Disconnected");
            Console.WriteLine($"Device {disconnectedImei} disconnected");
        }
    }

    private void SaveDeviceInfo(string imei, int port, string sourceAddress, string state, DateTime registrationDateTime, DateTime lastConnectedDateTime)
    {
        using (var connection = new SQLiteConnection(_connectionString))
        {
            connection.Open();
            string query = @"INSERT INTO Devices (IMEI, Port, SourceAddress, State, RegistrationDateTime, LastConnectedDateTime) 
                             VALUES (@IMEI, @Port, @SourceAddress, @State, @RegistrationDateTime, @LastConnectedDateTime)";
            SQLiteCommand command = new SQLiteCommand(query, connection);
            command.Parameters.AddWithValue("@IMEI", imei);
            command.Parameters.AddWithValue("@Port", port);
            command.Parameters.AddWithValue("@SourceAddress", sourceAddress);
            command.Parameters.AddWithValue("@State", state);
            command.Parameters.AddWithValue("@RegistrationDateTime", registrationDateTime.ToString("o"));
            command.Parameters.AddWithValue("@LastConnectedDateTime", lastConnectedDateTime.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    private void UpdateClientInfo(string imei, string sourceAddress)
    {
        using (var connection = new SQLiteConnection(_connectionString))
        {
            connection.Open();
            string query = "UPDATE Devices SET SourceAddress = @SourceAddress WHERE IMEI = @IMEI";
            SQLiteCommand command = new SQLiteCommand(query, connection);
            command.Parameters.AddWithValue("@IMEI", imei);
            command.Parameters.AddWithValue("@SourceAddress", sourceAddress);
            command.ExecuteNonQuery();
        }
    }

    private void UpdateDeviceState(string imei, string state)
    {
        using (var connection = new SQLiteConnection(_connectionString))
        {
            connection.Open();
            string query = "UPDATE Devices SET State = @State WHERE IMEI = @IMEI";
            SQLiteCommand command = new SQLiteCommand(query, connection);
            command.Parameters.AddWithValue("@IMEI", imei);
            command.Parameters.AddWithValue("@State", state);
            command.ExecuteNonQuery();
        }
    }

    private void UpdateDeviceLastConnected(string imei, DateTime lastConnectedDateTime)
    {
        using (var connection = new SQLiteConnection(_connectionString))
        {
            connection.Open();
            string query = "UPDATE Devices SET LastConnectedDateTime = @LastConnectedDateTime WHERE IMEI = @IMEI";
            SQLiteCommand command = new SQLiteCommand(query, connection);
            command.Parameters.AddWithValue("@IMEI", imei);
            command.Parameters.AddWithValue("@LastConnectedDateTime", lastConnectedDateTime.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    private void UpdateDeviceStateAndLastConnected(string imei, string state, string sourceAddress, DateTime lastConnectedDateTime)
    {
        using (var connection = new SQLiteConnection(_connectionString))
        {
            connection.Open();
            string query = "UPDATE Devices SET State = @State, SourceAddress = @SourceAddress, LastConnectedDateTime = @LastConnectedDateTime WHERE IMEI = @IMEI";
            SQLiteCommand command = new SQLiteCommand(query, connection);
            command.Parameters.AddWithValue("@IMEI", imei);
            command.Parameters.AddWithValue("@State", state);
            command.Parameters.AddWithValue("@SourceAddress", sourceAddress);
            command.Parameters.AddWithValue("@LastConnectedDateTime", lastConnectedDateTime.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listener.Stop();
        _listenerThread.Join();
        foreach (var deviceConnection in _registeredDevices.Values)
        {
            deviceConnection.StopListening();
        }
    }
}

class Program
{
    static void Main()
    {
        TcpServer server = new TcpServer();
        server.Start(11001);
        Console.WriteLine("Press Enter to stop the server...");
        Console.ReadLine();
        server.Stop();
    }
}
