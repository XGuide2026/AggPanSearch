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
    /// 创 建：admin
    /// 日 期：2026-11-04 03:11
    /// 描 述：业务类
    /// </summary>
    public class PanSourceBLL
    {
        private PanSourceService panSourceService = new PanSourceService();

        #region 获取数据
        public async Task<TData<List<PanSourceEntity>>> GetList(PanSourceListParam param)
        {
            TData<List<PanSourceEntity>> obj = new TData<List<PanSourceEntity>>();
            obj.Data = await panSourceService.GetList(param);
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<List<PanSourceEntity>>> GetPageList(PanSourceListParam param, Pagination pagination)
        {
            TData<List<PanSourceEntity>> obj = new TData<List<PanSourceEntity>>();
            obj.Data = await panSourceService.GetPageList(param, pagination);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 前台资源分类页分页列表（categoryId 大于 0 时按分类过滤）
        /// </summary>
        public async Task<TData<List<PanSourceEntity>>> GetCategoryList(int pageNo, int pageSize, long categoryId = 0)
        {
            TData<List<PanSourceEntity>> obj = new TData<List<PanSourceEntity>>();
            Pagination pagination = new Pagination
            {
                PageIndex = pageNo,
                PageSize = pageSize
            };
            obj.Data = await panSourceService.GetCategoryList(pagination, categoryId);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 按分类统计资源数量：分类ID → 资源条数，供前台分类导航的数量标签使用
        /// </summary>
        public async Task<Dictionary<int, int>> GetCategoryCounts()
        {
            return await panSourceService.GetCategoryCounts();
        }

        public async Task<TData<PanSourceEntity>> GetEntity(long id)
        {
            TData<PanSourceEntity> obj = new TData<PanSourceEntity>();
            obj.Data = await panSourceService.GetEntity(id);
            if (obj.Data != null)
            {
                obj.Tag = 1;
            }
            return obj;
        }
        #endregion

        #region 提交数据
        public async Task<TData<string>> SaveForm(PanSourceEntity entity)
        {
            TData<string> obj = new TData<string>();
            await panSourceService.SaveForm(entity);
            obj.Data = entity.Id.ParseToString();
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData> DeleteForm(string ids)
        {
            TData obj = new TData();
            await panSourceService.DeleteForm(ids);
            obj.Tag = 1;
            return obj;
        }
        #endregion

        #region 自动截图
        /// <summary>
        /// 启动截图任务：对选中的夸克资源做移动端截图，图片落盘后追加到 vod_pic 末尾（persist=true）；
        /// persist=false（资源图片生成弹窗暂存模式）时仅落盘并回传图片地址，不写库，由弹窗统一保存。
        /// ids 为逗号分隔的资源 Id；mode 为截图模式（1=打开文件夹显示文件列表截图）；批量模式非夸克与已有截图会跳过，暂存模式仅跳过非夸克。
        /// </summary>
        public TData<string> StartScreenshot(string ids, int mode = 1, bool persist = true)
        {
            TData<string> obj = new TData<string>();
            List<long> idList = new List<long>();
            if (!string.IsNullOrWhiteSpace(ids))
            {
                foreach (string id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(id.Trim(), out long value) && value > 0)
                    {
                        idList.Add(value);
                    }
                }
            }
            if (idList.Count == 0)
            {
                obj.Message = "请选择要截图的资源";
                return obj;
            }

            string jobId = PanSourceScreenshotJobRunner.Start(idList, mode, persist, out string error);
            if (jobId.Length == 0)
            {
                obj.Message = error;
                return obj;
            }
            obj.Data = jobId;
            obj.Tag = 1;
            obj.Message = persist ? "截图任务已提交，正在后台截图" : "图片生成任务已提交，完成后将加入图片列表";
            return obj;
        }

        /// <summary>查询截图进度；jobId 为空或已过期时返回最近一个运行中的任务。</summary>
        public TData<PanSourceScreenshotJobState> GetScreenshotProgress(string jobId)
        {
            TData<PanSourceScreenshotJobState> obj = new TData<PanSourceScreenshotJobState>();
            PanSourceScreenshotJobState state = PanSourceScreenshotJobManager.Get(jobId);
            if (state == null)
            {
                state = PanSourceScreenshotJobManager.GetLatestRunning();
            }
            if (state == null)
            {
                obj.Message = "截图任务不存在或已结束";
                return obj;
            }
            obj.Data = state;
            obj.Tag = 1;
            obj.Message = state.Message;
            return obj;
        }

        /// <summary>取消正在运行的截图任务（已保存的截图保留）。</summary>
        public TData CancelScreenshot(string jobId)
        {
            TData obj = new TData();
            if (!PanSourceScreenshotJobManager.Cancel(jobId))
            {
                obj.Message = "截图任务不存在或已结束";
                return obj;
            }
            obj.Tag = 1;
            obj.Message = "已提交取消请求，正在停止截图";
            return obj;
        }
        #endregion

        #region 私有方法
        #endregion
    }
}
