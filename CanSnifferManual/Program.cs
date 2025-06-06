﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class Message
{
    public uint ID { get; set; }
    public byte[] Data { get; set; }
    public DateTime Timestamp { get; set; }

    public ulong GetDataValue()
    {
        ulong value = 0;
        for (int i = 0; i < Data.Length; i++)
        {
            value = (value << 8) | Data[i];
        }
        return value;
    }

    public ushort GetBytePairValue(int index1, int index2)
    {
        if (index1 < 0 || index1 >= Data.Length || index2 < 0 || index2 >= Data.Length)
        {
            throw new ArgumentOutOfRangeException("Index out of range");
        }
        return (ushort)((Data[index1] << 8) | Data[index2]);
    }
}

public class Parameter
{
    public string Address { get; set; }
    public int[] ByteIndices { get; set; }
    public double Coefficient { get; set; }
    public string Name { get; set; }
}

class CANMessageLogger
{
    private static readonly object bufferLock = new object();
    private static List<Message> currentBuffer = new List<Message>();
    private static List<Message> successBuffer = new List<Message>();
    static List<string> targetAddresses = new List<string> { "136", "13A", "17C", "1DC" };
    static Dictionary<string, string> lastMessages = new Dictionary<string, string>();
    static Dictionary<string, string> lastParameterValues = new Dictionary<string, string>();

    static List<Parameter> parameters = new List<Parameter>
    {
        new Parameter { Address = "17C", ByteIndices = new[] { 2, 3 }, Coefficient = 1, Name = "RPM" }
        // Добавьте другие параметры здесь, например:
        // new Parameter { Address = "1A0", ByteIndices = new[] { 0, 1 }, Coefficient = 0.1, Name = "Давление" }
    };

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string portName = "COM11";
        int baudRate = 115200;
        string logFilename = $"can_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

        using (SerialPort serialPort = new SerialPort(portName, baudRate))
        using (StreamWriter logWriter = new StreamWriter(logFilename, append: true))
        {
            try
            {
                serialPort.Open();
                Console.WriteLine($"Подключено к {portName} на скорости {baudRate} бод. Логи записываются в {logFilename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка открытия порта: {ex.Message}");
                return;
            }

            var cts = new CancellationTokenSource();
            var readingTask = Task.Run(() => ReadSerial(serialPort, logWriter, cts.Token));

            Thread updateThread = new Thread(UpdateConsole);
            updateThread.IsBackground = true;
            updateThread.Start();

            Console.WriteLine("Нажмите 's' для старта, 'i' для фильтра увеличенных значений, 'k' для фильтра уменьшенных значений, 'q' для выхода.");

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).KeyChar;
                    if (key == 's')
                    {
                        lock (bufferLock)
                        {
                            successBuffer.Clear();
                            successBuffer.AddRange(currentBuffer);
                        }
                        Console.WriteLine($"Скопировано {successBuffer.Count} сообщений в successBuffer");
                        SaveSuccessBuffer();
                    }
                    else if (key == 'i')
                    {
                        UpdateSuccessBuffer(isIncrease: true);
                    }
                    else if (key == 'k')
                    {
                        UpdateSuccessBuffer(isIncrease: false);
                    }
                    else if (key == 'q')
                    {
                        cts.Cancel();
                        break;
                    }
                }
                Thread.Sleep(100);
            }

            await readingTask;
        }
    }

    static void ReadSerial(SerialPort serialPort, StreamWriter logWriter, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                string line = serialPort.ReadLine();
                logWriter.WriteLine(line);
                logWriter.Flush();

                var message = ParseMessage(line);
                if (message != null)
                {
                    lock (bufferLock)
                    {
                        var threshold = DateTime.Now.AddMilliseconds(-1000);
                        while (currentBuffer.Count > 0 && currentBuffer[0].Timestamp < threshold)
                        {
                            currentBuffer.RemoveAt(0);
                        }
                        currentBuffer.Add(message);
                    }

                    string idHex = message.ID.ToString("X");
                    if (targetAddresses.Contains(idHex))
                    {
                        lock (lastMessages)
                        {
                            lastMessages[idHex] = string.Join(" ", message.Data.Select(b => b.ToString("D")));
                        }
                    }

                    foreach (var param in parameters)
                    {
                        if (idHex == param.Address)
                        {
                            if (param.ByteIndices.All(i => i < message.Data.Length))
                            {
                                ulong value = 0;
                                foreach (var index in param.ByteIndices)
                                {
                                    value = (value << 8) | message.Data[index];
                                }
                                double calculatedValue = value * param.Coefficient;
                                lock (lastParameterValues)
                                {
                                    lastParameterValues[param.Name] = calculatedValue.ToString();
                                }
                                Console.WriteLine($"{param.Name}: {calculatedValue}   ");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("either a thread exit"))
                {
                    Thread.Sleep(2);
                    continue;
                }
                Console.WriteLine($"Ошибка чтения порта: {ex.Message}");
            }
        }
    }

    static Message ParseMessage(string line)
    {
        try
        {
            var parts = line.Split(':');
            if (parts.Length == 3)
            {
                uint id = uint.Parse(parts[1], System.Globalization.NumberStyles.HexNumber);
                var dataStr = parts[2].Split(' ');
                byte[] data = dataStr.Select(s => byte.Parse(s, System.Globalization.NumberStyles.HexNumber)).ToArray();
                return new Message { ID = id, Data = data, Timestamp = DateTime.Now };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка парсинга сообщения: {ex.Message}");
        }
        return null;
    }

    static Dictionary<uint, Message> GetLatestPerId(List<Message> messages)
    {
        var dict = new Dictionary<uint, Message>();
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (!dict.ContainsKey(msg.ID))
            {
                dict[msg.ID] = msg;
            }
        }
        return dict;
    }

    static void UpdateSuccessBuffer(bool isIncrease)
    {
        lock (bufferLock)
        {
            var currentLatest = GetLatestPerId(currentBuffer);
            var successLatest = GetLatestPerId(successBuffer);
            successBuffer.Clear();

            foreach (var kvp in currentLatest)
            {
                uint id = kvp.Key;
                if (successLatest.ContainsKey(id))
                {
                    Message currentMsg = kvp.Value;
                    Message successMsg = successLatest[id];

                    for (int i = 0; i < currentMsg.Data.Length - 1; i++)
                    {
                        ushort currentPairValue = currentMsg.GetBytePairValue(i, i + 1);
                        ushort successPairValue = successMsg.GetBytePairValue(i, i + 1);

                        if ((isIncrease && currentPairValue > successPairValue) || (!isIncrease && currentPairValue < successPairValue))
                        {
                            successBuffer.Add(currentMsg);
                            Console.WriteLine($"Найдена пара байт {i} и {i + 1} для ID {id:X}: Предыдущее = {successPairValue}, Текущее = {currentPairValue}");
                            break;
                        }
                    }
                }
            }
        }
        Console.WriteLine($"Обновлен successBuffer: {successBuffer.Count} сообщений ({(isIncrease ? "увеличенные" : "уменьшенные")})");
        SaveSuccessBuffer();
    }

    static void SaveSuccessBuffer()
    {
        string filename = $"successBuffer_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        using (var writer = new StreamWriter(filename))
        {
            foreach (var msg in successBuffer)
            {
                string dataStr = string.Join(" ", msg.Data.Select(b => b.ToString("X2")));
                writer.WriteLine($"ID:{msg.ID:X}:{dataStr}");
            }
        }
        Console.WriteLine($"Сохранено в {filename}");
    }

    static void UpdateConsole()
    {
        while (true)
        {
            lock (lastMessages)
            {
                Console.SetCursorPosition(0, Console.WindowHeight - 4 - parameters.Count - 1);
                foreach (var addr in targetAddresses)
                {
                    if (lastMessages.TryGetValue(addr, out string data))
                    {
                        Console.WriteLine($"{addr}: {data}              ");
                    }
                    else
                    {
                        Console.WriteLine($"{addr}: Нет данных");
                    }
                }

                Console.WriteLine("\nПараметры:");
                foreach (var param in parameters)
                {
                    if (lastParameterValues.TryGetValue(param.Name, out string value))
                    {
                        Console.WriteLine($"{param.Name}: {value}");
                    }
                    else
                    {
                        Console.WriteLine($"{param.Name}: Нет данных");
                    }
                }
            }
            Thread.Sleep(1000);
        }
    }
}