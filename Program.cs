using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Neo.VM;
using ExecutionContext = Neo.VM.ExecutionContext;
using StackItem = Neo.VM.Types.StackItem;

/*

My idea is to use the C# async/await infrastructure for scenarios where a native contract invokes a
VM contract.

This problem seems very similar to using async/await to avoid blocking the UI thread of a client app.
In most UI frameworks, there is a single dedicated thread for servicing the UI components. If you block
this thread - say with a long running IO operation - the UI becomes unresponsive. With async/await,
these long running operations return a Task/Task<T> and are awaited by code running on the UI thread.
When the UI thread encounters the awaited Task, it exits the method that started the long running
operation and continues processing UI events. When the long running operation completes, the remainder
of the async method where the await occurred is scheduled to be executed on the UI thread.

We see something similar with Native contracts that invoke CallFromNativeContract. Calling StepOut in a
loop until we return to the execution context where the native contract was invoked is similar to 
blocking the UI thread on a long running operation. In the UI thread scenario, we want the UI thread to 
return to the UI processing loop to keep the UI responsive until the task completes. In the Neo VM scenario, 
we want the execution thread to return to the debugger loop so the user can control program execution. 

So can we use C#'s async/await infrastructure here? Yes, we can. C# 7 introduced new infrastructure to 
enable developers to build "task-like types" so that async/await can be used with types other than the 
built in Task/Task<T> types.

The sample code below is a stripped down example using NeoVM (but not neo.dll). In this example, 
CallFromNativeContract returns a "ContractTask" type, a "task-like type" that can be awaited, but that
doesn't spin up threads from the thread pool to complete it's operations. Rather, the ContractTask gets
associated with the executionContext associated with the VM script being called. When that context is
unloaded, the remaining code associated with that ContractTask is executed.
*/

namespace await_test
{
    // a type is awaitable if it has a GetAwaiter method. Having these as extension methods eliminates
    // and issue with instance methods colliding on return type
    static class Extensions
    {
        public static ContractTaskAwaiter GetAwaiter(this ContractTask @this) => new ContractTaskAwaiter(@this);
        public static ContractTaskAwaiter<T> GetAwaiter<T>(this ContractTask<T> @this) => new ContractTaskAwaiter<T>(@this);
    }

    // Models a VM contract execution that does not return a value (like Task)
    class ContractTask
    {
        private Action? continuation = null;
        public bool IsCompleted { get; private set; } = false;

        public void OnCompleted(Action continuation)
        {
            var value = Interlocked.CompareExchange<Action?>(ref this.continuation, continuation, null);
        }

        public virtual void SetResult(StackItem stackItem)
        {
            throw new Exception();
        }

        public void RunContinuation()
        {
            if (continuation == null || IsCompleted)
            {
                throw new Exception();
            }
            else
            {
                continuation();
                IsCompleted = true;
            }
        }
    }

    // implements type needed by await keyword
    class ContractTaskAwaiter : INotifyCompletion
    {
        private readonly ContractTask contractTask;

        public ContractTaskAwaiter(ContractTask contractTask)
        {
            this.contractTask = contractTask;
        }

        public bool IsCompleted => this.contractTask.IsCompleted;
        public void GetResult() {; }
        public void OnCompleted(Action continuation) => contractTask.OnCompleted(continuation);
    }

    // Models a VM contract execution that returns a value (like Task<T>)
    class ContractTask<T> : ContractTask
    {
        public T? Value { get; private set; }

        public override void SetResult(StackItem stackItem)
        {
            if (typeof(T) == typeof(string))
            {
                Value = (T)(object)stackItem.GetString();
            }
            else
            {
                throw new Exception();
            }
        }
    }

    // implements type needed by await keyword
    class ContractTaskAwaiter<T> : INotifyCompletion
    {
        private readonly ContractTask<T> contractTask;

        public ContractTaskAwaiter(ContractTask<T> contractTask)
        {
            this.contractTask = contractTask;
        }

        public bool IsCompleted => this.contractTask.IsCompleted;

        public T GetResult() => contractTask.Value ?? throw new Exception();

        public void OnCompleted(Action continuation)
        {
            contractTask.OnCompleted(continuation);
        }
    }

    // Stripped down ApplicationEngine for test purposes
    class AppEngine : ExecutionEngine
    {
        protected override async void OnSysCall(uint method)
        {
            switch (method)
            {
                // fake SysCall method that "invokes" a VM script and waits for the script to complete before continuting
                case 1:
                    {
                        Console.WriteLine($"OnSysCall FAKE AWAIT CALL before");
                        await CallFromNativeContract();
                        Console.WriteLine($"OnSysCall FAKE AWAIT CALL after");
                    }
                    break;
                // fake SysCall method that "invokes" a VM script, waits for the script to complete before continuting
                // and receives a result
                case 2:
                    {
                        Console.WriteLine($"OnSysCall FAKE AWAIT RESULT CALL before");
                        var result = await CallFromNativeContract("Paddington");
                        Console.WriteLine($"OnSysCall FAKE AWAIT RESULT CALL after: {result}");
                    }
                    break;
                // All other syscalls, simply echo value to console
                default:
                    Console.WriteLine($"OnSysCall {method}");
                    break;
            }
        }

        // track outstanding contractTasks awaiting completion
        Dictionary<ExecutionContext, ContractTask> contractTasks = new Dictionary<ExecutionContext, ContractTask>();

        protected override void ContextUnloaded(ExecutionContext context)
        {
            // if there's an contract task associated with this context, run it
            if (contractTasks.TryGetValue(context, out var task))
            {
                if (CurrentContext.EvaluationStack.Count > 0)
                {
                    task.SetResult(Pop());
                }
                task.RunContinuation();
                contractTasks.Remove(context);
            }

            base.ContextUnloaded(context);
        }

        // Fake CallFromNativeContract loads a script that invokes SysCall 100 and returns
        internal ContractTask CallFromNativeContract()
        {
            using var sb = new ScriptBuilder();
            sb.EmitSysCall(100);
            sb.Emit(OpCode.RET);
            var script = sb.ToArray();

            var ctx = LoadScript(script);
            var task = new ContractTask();
            contractTasks.Add(ctx, task);
            return task;
        }

        // Fake CallFromNativeContract loads a script that invokes SysCall 200, pushes the string
        // value onto the VM execution stack and returns
        internal ContractTask<string> CallFromNativeContract(string value)
        {
            using var sb = new ScriptBuilder();
            sb.EmitSysCall(200);
            sb.EmitPush(value);
            sb.Emit(OpCode.RET);
            var script = sb.ToArray();

            var ctx = LoadScript(script);
            var task = new ContractTask<string>();
            contractTasks.Add(ctx, task);
            return task;
        }

        // this is only here for debug output
        protected override void PreExecuteInstruction()
        {
            Console.WriteLine($"{InvocationStack.Count} {CurrentContext.GetHashCode()} {CurrentContext.InstructionPointer} {CurrentContext.CurrentInstruction.OpCode}");
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            using var sb = new ScriptBuilder();
            sb.EmitSysCall(0);
            sb.EmitSysCall(1);
            sb.EmitSysCall(2);
            sb.EmitSysCall(3);
            sb.Emit(OpCode.RET);

            var engine = new AppEngine();
            engine.LoadScript(sb.ToArray());
            engine.Execute();
        }
    }
}
