using System;
using System.Threading.Tasks;

namespace Migration
{
    // Default Outcome represents default TResult: reasonable.
    // I could define good Equals, GetHashCode, ToString here, but they
    // would have to call the "better" ones on the fields (or accept
    // implementations for the fields as parameters), which would be weird.
    public struct Outcome<TResult, TException> : IOutcome<TResult, TException>
        where TException : Exception
    {
        private readonly TResult result;
        private readonly TException exception;

        public Outcome(TResult result)
        {
            this.result = result;
            this.exception = null;
        }
        public Outcome(TException exception)
        {
            this.result = default(TResult);
            this.exception = exception;
        }

        public TResult Result { get { return result; } }
        public TException Exception { get { return exception; } }
    }

    // For introspection by BetterComparator, etc. without wildcard capture.
    public interface IOutcome<out TResult, out TException> where TException : Exception
    {
        TResult Result { get; }
        TException Exception { get; }
    }

    public static class Catching<TException> where TException : Exception
    {
        public static Outcome<TResult, TException> Run<TResult>(Func<TResult> func)
        {
            TResult result;
            try
            {
                result = func();
            }
            catch (TException ex)
            {
                return new Outcome<TResult, TException>(ex);
            }
            return new Outcome<TResult, TException>(result);
        }
        public static async Task<Outcome<TResult, TException>> Task<TResult>(Task<TResult> task)
        {
            TResult result;
            try
            {
                result = await task;
            }
            catch (TException ex)
            {
                return new Outcome<TResult, TException>(ex);
            }
            return new Outcome<TResult, TException>(result);
        }
    }

    public static class OutcomeStatics
    {
        public static void SetOutcome<TResult, TException>(
            this TaskCompletionSource<TResult> tcs, Outcome<TResult, TException> outcome)
            where TException : Exception
        {
            if (outcome.Exception != null)
                tcs.SetException(outcome.Exception);
            else
                tcs.SetResult(outcome.Result);
        }
    }
}
