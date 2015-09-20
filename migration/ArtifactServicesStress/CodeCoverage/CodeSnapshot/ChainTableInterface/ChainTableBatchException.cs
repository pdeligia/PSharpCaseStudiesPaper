using System;
using System.Runtime.Serialization;

namespace Microsoft.WindowsAzure.Storage.ChainTableInterface
{
    [Serializable]
    public class ChainTableBatchException : StorageException
    {
        public int FailedOpIndex { get; private set; }

        public ChainTableBatchException(int failedOpIndex, StorageException storageEx)
            : base(storageEx.RequestInformation,
                  string.Format("{0}: {1}", failedOpIndex, storageEx.Message),
                  storageEx.InnerException)
        {
            this.FailedOpIndex = failedOpIndex;
        }

        protected ChainTableBatchException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            FailedOpIndex = info.GetInt32("FailedOpIndex");
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue("FailedOpIndex", FailedOpIndex);
        }
    }
}
