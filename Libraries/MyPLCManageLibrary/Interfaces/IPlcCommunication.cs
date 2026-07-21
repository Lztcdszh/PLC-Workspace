public interface IPlcCommunication : IDisposable
{
    bool IsConnected { get; }
    Task<bool> ConnectAsync();
    Task DisconnectAsync();
    Task<T> ReadAsync<T>(string address);
    Task<bool> WriteAsync<T>(string address, T value);
    Task<bool> CheckConnectionAsync();
    Task SendHeartbeatAsync();
    
}