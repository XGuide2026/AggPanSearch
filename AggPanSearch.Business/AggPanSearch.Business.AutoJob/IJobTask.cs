using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Business.AutoJob
{
    public interface IJobTask
    {
        Task<TData> Start();
    }
}
