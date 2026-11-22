using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Service.PanManage;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：TG频道业务类（pan_search_channel）
    /// </summary>
    public class PanSearchChannelBLL
    {
        private PanSearchChannelService panSearchChannelService = new PanSearchChannelService();

        #region 获取数据
        public async Task<TData<List<PanSearchChannelEntity>>> GetList(PanSearchChannelListParam param)
        {
            TData<List<PanSearchChannelEntity>> obj = new TData<List<PanSearchChannelEntity>>();
            obj.Data = await panSearchChannelService.GetList(param);
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<List<PanSearchChannelEntity>>> GetPageList(PanSearchChannelListParam param, Pagination pagination)
        {
            TData<List<PanSearchChannelEntity>> obj = new TData<List<PanSearchChannelEntity>>();
            obj.Data = await panSearchChannelService.GetPageList(param, pagination);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<PanSearchChannelEntity>> GetEntity(long id)
        {
            TData<PanSearchChannelEntity> obj = new TData<PanSearchChannelEntity>();
            obj.Data = await panSearchChannelService.GetEntity(id);
            if (obj.Data != null)
            {
                obj.Tag = 1;
            }
            return obj;
        }

        /// <summary>
        /// 前台搜索使用的频道名列表（status=1）。空列表由调用方回退到 AggPanSearch 内置默认频道。
        /// </summary>
        public async Task<TData<List<string>>> GetEnabledNames()
        {
            TData<List<string>> obj = new TData<List<string>>();
            obj.Data = await panSearchChannelService.GetEnabledNames();
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }
        #endregion

        #region 提交数据
        public async Task<TData<string>> SaveForm(PanSearchChannelEntity entity)
        {
            TData<string> obj = new TData<string>();
            if (entity == null)
            {
                obj.Message = "参数不能为空";
                return obj;
            }
            entity.Name = PanSearchChannelService.NormalizeName(entity.Name);
            if (entity.Name.Length == 0)
            {
                obj.Message = "频道名不能为空";
                return obj;
            }
            if (panSearchChannelService.ExistName(entity))
            {
                obj.Message = "频道「" + entity.Name + "」已存在";
                return obj;
            }
            if (entity.Status == null)
            {
                entity.Status = 1;
            }
            if (entity.Sort == null)
            {
                entity.Sort = 0;
            }
            await panSearchChannelService.SaveForm(entity);
            obj.Data = entity.Id.ParseToString();
            obj.Tag = 1;
            obj.Message = "保存成功";
            return obj;
        }

        public async Task<TData> DeleteForm(string ids)
        {
            TData obj = new TData();
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                obj.Message = "请选择要删除的频道";
                return obj;
            }
            TData<List<PanSearchChannelEntity>> listObj = await GetList(null);
            List<PanSearchChannelEntity> sysList = (listObj.Data ?? new List<PanSearchChannelEntity>())
                .Where(t => t.Id.HasValue && idArr.Contains(t.Id.Value) && t.IsSys == 1)
                .ToList();
            if (sysList.Count > 0)
            {
                obj.Message = "内置频道不允许删除：" + string.Join("、", sysList.Select(t => t.Name));
                return obj;
            }
            await panSearchChannelService.DeleteForm(ids);
            obj.Tag = 1;
            obj.Message = "删除成功";
            return obj;
        }

        /// <summary>
        /// 保存频道探测结果（是否可访问 + 最近一次网盘资源时间）
        /// </summary>
        public async Task SaveProbeResult(long id, bool accessible, DateTime? lastUpdate)
        {
            await panSearchChannelService.SaveProbeResult(id, accessible, lastUpdate);
        }

        /// <summary>
        /// 重置系统默认频道：清空表数据后重新写入系统默认频道（全部启用）
        /// </summary>
        public async Task<TData<int>> ResetDefaults(List<string> names)
        {
            TData<int> obj = new TData<int>();
            obj.Data = await panSearchChannelService.ResetDefaults(names);
            if (obj.Data > 0)
            {
                obj.Tag = 1;
                obj.Message = "已重置为 " + obj.Data + " 个系统默认频道（全部启用）";
            }
            else
            {
                obj.Message = "系统默认频道清单为空，未执行重置";
            }
            return obj;
        }

        /// <summary>
        /// 批量切换启用状态（后台列表用）
        /// </summary>
        public async Task<TData> SetStatus(string ids, int status)
        {
            TData obj = new TData();
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                obj.Message = "请选择频道";
                return obj;
            }
            foreach (long id in idArr)
            {
                PanSearchChannelEntity entity = await panSearchChannelService.GetEntity(id);
                if (entity == null || entity.Id.IsNullOrZero())
                {
                    continue;
                }
                entity.Status = status;
                await panSearchChannelService.SaveForm(entity);
            }
            obj.Tag = 1;
            obj.Message = status == 1 ? "已启用" : "已停用";
            return obj;
        }
        #endregion
    }
}
