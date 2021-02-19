using System;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Neo.VM;

namespace await_test
{
    [AsyncMethodBuilder(typeof(ContractTaskMethodBuilder))]
    class ContractTask
    {
        public ContractTaskAwaiter GetAwaiter() => throw new NotImplementedException();
    }

    class ContractTaskAwaiter : INotifyCompletion
    {
        public bool IsCompleted => throw new NotImplementedException();
        public void GetResult() => throw new NotImplementedException();
        public void OnCompleted(Action continuation) => throw new NotImplementedException();
    }

    class ContractTaskMethodBuilder
    {
        public static ContractTaskMethodBuilder Create() => throw new NotImplementedException();
        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
             => throw new NotImplementedException();
        public void SetStateMachine(IAsyncStateMachine stateMachine) => throw new NotImplementedException();
        public void SetException(Exception exception) => throw new NotImplementedException();
        public void SetResult() => throw new NotImplementedException();
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => throw new NotImplementedException();
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => throw new NotImplementedException();
        public ContractTask Task => throw new NotImplementedException();
    }


    [AsyncMethodBuilder(typeof(ContractTaskMethodBuilder<>))]
    class ContractTask<T>
    {
        public ContractTaskAwaiter<T> GetAwaiter() => throw new NotImplementedException();
    }

    class ContractTaskAwaiter<T> : INotifyCompletion
    {
        public bool IsCompleted => throw new NotImplementedException();
        public void GetResult() => throw new NotImplementedException();
        public void OnCompleted(Action continuation) => throw new NotImplementedException();
    }

    class ContractTaskMethodBuilder<T>
    {
        public static ContractTaskMethodBuilder<T> Create() => throw new NotImplementedException();
        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
             => throw new NotImplementedException();
        public void SetStateMachine(IAsyncStateMachine stateMachine) => throw new NotImplementedException();
        public void SetException(Exception exception) => throw new NotImplementedException();
        public void SetResult() => throw new NotImplementedException();
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => throw new NotImplementedException();
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => throw new NotImplementedException();
        public ContractTask<T> Task => throw new NotImplementedException();
    }

    class AppEngine : ExecutionEngine
    {
        protected override void OnSysCall(uint method)
        {
            CallFromNativeContract();
        }

        internal void CallFromNativeContract()
        {
            using var sb = new ScriptBuilder();
            sb.Emit(OpCode.NOP);
            sb.Emit(OpCode.NOP);
            sb.Emit(OpCode.RET);
            var script = sb.ToArray();

            var ctx = LoadScript(script);
        }

        public new VMState Execute()
        {
            if (State == VMState.BREAK)
                State = VMState.NONE;
            while (State != VMState.HALT && State != VMState.FAULT)
                ExecuteNext();
            return State;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            using var sb = new ScriptBuilder();
            sb.Emit(OpCode.NOP);
            sb.EmitSysCall(1234);
            sb.Emit(OpCode.NOP);
            sb.Emit(OpCode.RET);

            var engine = new AppEngine();
            engine.LoadScript(sb.ToArray());
            engine.Execute();


        }
    }
}
