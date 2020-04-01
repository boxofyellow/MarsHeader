#if (USESYSTEM)
using System.Data.SqlClient;
#else
using Microsoft.Data.SqlClient;
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarsHeader
{
    class Program
    {
        private const int NumberOfTasks = 100;  // How many attempts to poison the connection pool we will try
        private const int NumberOfNonPoisoned = 10;  // Number of normal requests for each attempt 

        static void Main(string[] args)
        {
            string connectionString = args?.FirstOrDefault() ?? "Server=localhost;Database=master;Integrated Security=true";
            Console.WriteLine(NumberOfTasks);

#if (USESYSTEM)
            Console.WriteLine("UseSystem IS set");
#else
            Console.WriteLine("UseSystem is NOT set");
#endif

            using (var connection = new SqlConnection(connectionString))
            {
                Console.WriteLine(connection.GetType().FullName);
            }

            s_watch = Stopwatch.StartNew();
            s_random = new Random(4); // chosen via fair dice role.
            try
            {
                // Setup a timer so that we can see what is going on while our tasks run
                using (new Timer(TimerCallback, state: null, dueTime: TimeSpan.FromSeconds(5), period: TimeSpan.FromSeconds(5)))
                {
                    Parallel.For(
                        fromInclusive: 0,
                        toExclusive: NumberOfTasks,
                        (int i) => DoManyAsync(connectionString).GetAwaiter().GetResult());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            DisplaySummary();
            foreach (var detail in s_exceptionDetails)
            {
                Console.WriteLine(detail);
            }
        }

        // Display one row every 5'ish seconds
        private static void TimerCallback(object state)
        {
            lock (s_lockObject)
            {
                DisplaySummary();
            }
        }

        private static void DisplaySummary()
        {
            int count;
            lock (s_exceptionDetails) 
            {
                count = s_exceptionDetails.Count;
            }

            Console.WriteLine($"{s_watch.Elapsed} {s_continue} S:{s_start} D:{s_done} I:{s_inFlight} R:{s_rowsRead} D:{s_resultRead} P:{s_poisonedEnded} E:{s_nonPoisonedExceptions} L:{s_poisonCleanUpExceptions} U:{count} F:{s_found}");
        }

        // This is the the main body that our Tasks run
        private static async Task DoManyAsync(string connectionString)
        {
            Interlocked.Increment(ref s_start);
            Interlocked.Increment(ref s_inFlight);

            // First poison
            await DoOneAsync(connectionString, poison: true);

            for (int i = 0; i < NumberOfNonPoisoned && s_continue; i++)
            {
                // now run some without poisoning
                await DoOneAsync(connectionString);
            }

            Interlocked.Decrement(ref s_inFlight);
            Interlocked.Increment(ref s_done);
        }

        // This will do our work, open a connection, and run a query (that returns 4 results sets)
        // if we are poisoning we will 
        //   1 - Interject some sleeps in the sql statement so that it will run long enough that we can cancel it
        //   2 - Setup a time bomb task that will cancel the command a random amount of time later
        private static async Task DoOneAsync(string connectionString, bool poison = false)
        {
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    StringBuilder builder = new StringBuilder();
                    for (int i = 0; i < 4; i++)
                    {
                        builder.AppendLine("SELECT name FROM sys.tables");
                        if (poison && i < 3)
                        {
                            builder.AppendLine("WAITFOR DELAY '00:00:01'");
                        }
                    }

                    int rowsRead = 0;
                    int resultRead = 0;

                    try
                    {
                        await connection.OpenAsync();
                        using (var command = connection.CreateCommand())
                        {
                            Task timeBombTask = default;
                            try
                            {
                                // Setup our time bomb
                                if (poison)
                                {
                                    timeBombTask = TimeBombAsync(command);
                                }

                                command.CommandText = builder.ToString();

                                // Attempt to read all of the data
                                using (var reader = await command.ExecuteReaderAsync())
                                {
                                    try
                                    {
                                        do
                                        {
                                            resultRead++;
                                            while (await reader.ReadAsync() && s_continue)
                                            {
                                                rowsRead++;
                                            }
                                        } 
                                        while(await reader.NextResultAsync() && s_continue);
                                    }
                                    catch when (poison)
                                    {
                                        //  This looks a little strange, we failed to read above so this should fail too
                                        //  But consider the case where this code is elsewhere (in the Dispose method of a class holding this logic)
                                        try
                                        {
                                            while (await reader.NextResultAsync())
                                            {
                                            }
                                        }
                                        catch
                                        {
                                            Interlocked.Increment(ref s_poisonCleanUpExceptions);
                                        }

                                        throw;
                                    }
                                }
                            }
                            finally
                            {
                                // Make sure to clean up our time bomb
                                // It is unlikely, but the timebomb may get delayed in the Task Queue
                                // And we don't want it running after we dispose the command
                                if (timeBombTask != default)
                                {
                                    await timeBombTask;
                                }
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref s_rowsRead, rowsRead);
                        Interlocked.Add(ref s_resultRead, resultRead);
                        if (poison)
                        {
                            Interlocked.Increment(ref s_poisonedEnded);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!poison)
                {
                    Interlocked.Increment(ref s_nonPoisonedExceptions);

                    string details = ex.ToString();
                    details = details.Substring(0, Math.Min(200, details.Length));
                    lock (s_exceptionDetails)
                    {
                        s_exceptionDetails.Add(details);
                    }
                }

                if (ex.Message.Contains("The MARS TDS header contained errors."))
                {
                    s_continue = false;
                    if (s_found == 0) // This check is not really safe we may list more than one.
                    {
                        lock (s_lockObject)
                        {
                            // You will notice that poison will be likely be false here, it is the normal commands that suffer
                            // Once we have successfully poisoned the connection pool, we may start to see some other request to poison fail just like the normal requests
                            Console.WriteLine($"{poison} {DateTime.UtcNow.ToString("O")}");
                            Console.WriteLine(ex);
                        }
                    }
                    Interlocked.Increment(ref s_found);
                }
            }
        }

        private static async Task TimeBombAsync(SqlCommand command)
        {
            await SleepAsync(100, 3000);
            command.Cancel();
        }

        private static async Task SleepAsync(int minMs, int maxMs)
        {
            int delayMs;
            lock (s_random)
            {
                delayMs = s_random.Next(minMs, maxMs);
            }
            await Task.Delay(delayMs);
        }

        private static Stopwatch s_watch;

        private static int s_inFlight;
        private static int s_start;
        private static int s_done;
        private static int s_rowsRead;
        private static int s_resultRead;
        private static int s_nonPoisonedExceptions;
        private static int s_poisonedEnded;
        private static int s_poisonCleanUpExceptions;
        private static bool s_continue = true;
        private static int s_found;
        private static Random s_random;
        private static object s_lockObject = new object();

        private static HashSet<string> s_exceptionDetails = new HashSet<string>();
    }
}
