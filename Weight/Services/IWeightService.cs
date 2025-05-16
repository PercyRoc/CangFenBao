using System;
using System.Threading.Tasks;
using Weight.Models;

namespace Weight.Services;

public interface IWeightService : IDisposable
{
    Task<WeightData?> GetCurrentWeightAsync();
    bool IsConnected { get; }
    Task ConnectAsync();
    IObservable<WeightData?> WeightDataStream { get; }

    event Action<string, bool>? ConnectionChanged;
}