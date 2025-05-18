using System;
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
}

class CANMessageLogger
{
    private static readonly object bufferLock = new object();
    private static List<Message> currentBuffer = new List<Message>();
    private static List<Message> successBuffer = new List<Message>();
    // Список целевых адресов в шестнадцатеричном формате
    static List<string> targetAddresses = new List<string> { "136", "13A", "17C", "1DC" };
    // Словарь для хранения последних данных по адресам
    static Dictionary<string, string> lastMessages = new Dictionary<string, string>();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string portName = "COM11"; // Замените на ваш COM-порт
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

            // Запуск фонового потока для обновления консоли
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
                Thread.Sleep(100); // Избегаем активного ожидания
            }

            await readingTask; // Ждем завершения задачи чтения
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
                logWriter.Flush(); // Убеждаемся, что данные записаны в файл

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

                    // Сохраняем данные для целевых адресов
                    string idHex = message.ID.ToString("X");
                    if (targetAddresses.Contains(idHex))
                    {
                        lock (lastMessages)
                        {
                            lastMessages[idHex] = string.Join(" ", message.Data.Select(b => b.ToString("D")));
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
                    ulong currentValue = kvp.Value.GetDataValue();
                    ulong successValue = successLatest[id].GetDataValue();
                    if ((isIncrease && currentValue > successValue) || (!isIncrease && currentValue < successValue))
                    {
                        successBuffer.Add(kvp.Value);
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
                // Устанавливаем курсор на последние 4 строки консоли
                Console.SetCursorPosition(0, Console.WindowHeight - 4);
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
            }
            Thread.Sleep(1000); // Обновляем каждую секунду
        }
    }
}