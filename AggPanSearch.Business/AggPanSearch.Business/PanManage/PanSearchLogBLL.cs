using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：搜索日志业务逻辑（pan_search_log）
    /// </summary>
    public class PanSearchLogBLL
    {
        private PanSearchLogService panSearchLogService = new PanSearchLogService();

        #region 获取数据

        public async Task<TData<List<PanSearchLogEntity>>> GetList(PanSearchLogListParam param)
        {
            TData<List<PanSearchLogEntity>> obj = new TData<List<PanSearchLogEntity>>();
            obj.Data = await panSearchLogService.GetList(param);
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<List<PanSearchLogEntity>>> GetPageList(PanSearchLogListParam param, Pagination pagination)
        {
            TData<List<PanSearchLogEntity>> obj = new TData<List<PanSearchLogEntity>>();
            obj.Data = await panSearchLogService.GetPageList(param, pagination);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<PanSearchLogEntity>> GetEntity(long id)
        {
            TData<PanSearchLogEntity> obj = new TData<PanSearchLogEntity>();
            obj.Data = await panSearchLogService.GetEntity(id);
            if (obj.Data == null)
            {
                obj.Message = "请求失败，日志不存在";
            }
            else
            {
                obj.Tag = 1;
            }
            return obj;
        }

        /// <summary>
        /// 今日搜索量
        /// </summary>
        public async Task<TData<int>> GetTodayCount()
        {
            TData<int> obj = new TData<int>();
            obj.Data = await panSearchLogService.GetTodayCount();
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 总搜索量
        /// </summary>
        public async Task<TData<int>> GetTotalCount()
        {
            TData<int> obj = new TData<int>();
            obj.Data = await panSearchLogService.GetTotalCount();
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 平均搜索耗时（毫秒，pan_search_log.cost_ms 平均值，含缓存命中）
        /// </summary>
        public async Task<TData<int>> GetAvgCost()
        {
            TData<int> obj = new TData<int>();
            obj.Data = await panSearchLogService.GetAvgCost();
            obj.Tag = 1;
            return obj;
        }

        #endregion

        #region 提交数据

        public async Task<TData<string>> SaveForm(PanSearchLogEntity entity)
        {
            TData<string> obj = new TData<string>();
            await panSearchLogService.SaveForm(entity);
            obj.Data = entity.Id.ToString();
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 写入搜索日志（前台搜索接口调用，失败不影响搜索本身）
        /// </summary>
        /// <param name="keyword">搜索关键词</param>
        /// <param name="ip">搜索来源IP</param>
        /// <param name="isCache">是否命中缓存</param>
        /// <param name="resultTotal">搜索结果总数</param>
        /// <param name="panTypeResults">分类网盘搜索结果，形如 夸克网盘:12;百度网盘:3</param>
        /// <param name="costMs">搜索耗时（毫秒）</param>
        public async Task<TData> AddLog(string keyword, string ip, bool isCache = false, int resultTotal = 0,
            string panTypeResults = null, int costMs = 0)
        {
            TData obj = new TData();
            try
            {
                await panSearchLogService.AddLog(keyword, ip, isCache, resultTotal, panTypeResults, costMs);
                obj.Tag = 1;
            }
            catch (Exception ex)
            {
                obj.Tag = 0;
                obj.Message = ex.Message;
            }
            return obj;
        }

        public async Task<TData> DeleteForm(string ids)
        {
            TData obj = new TData();
            if (string.IsNullOrWhiteSpace(ids))
            {
                obj.Message = "参数错误，未选择要删除的日志";
                return obj;
            }
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr.Length == 0)
            {
                obj.Message = "参数错误，未选择要删除的日志";
                return obj;
            }
            await panSearchLogService.DeleteForm(idArr);
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 清空搜索日志
        /// </summary>
        public async Task<TData> ClearForm()
        {
            TData obj = new TData();
            await panSearchLogService.ClearForm();
            obj.Tag = 1;
            return obj;
        }

        #endregion
    }
}
