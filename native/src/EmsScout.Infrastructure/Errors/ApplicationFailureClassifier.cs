using System.Net.Http;
using System.Text.Json;
using EmsScout.Application.Errors;
using EmsScout.Application.Workflows;
using EmsScout.Infrastructure.Importing;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sidecar;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Errors;

public static class ApplicationFailureClassifier
{
    public static ApplicationFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = Unwrap(exception);
        return error switch
        {
            WorkflowExecutionException workflow => ClassifyWorkflow(workflow),
            OperationCanceledException => Failure(
                "operation_cancelled",
                ApplicationErrorCategory.Cancelled,
                "操作已取消",
                "操作已停止。",
                "可以重新开始操作。",
                true,
                error),
            CollectionSnapshotContractException => Failure(
                "snapshot_contract_invalid",
                ApplicationErrorCategory.Contract,
                "采集快照格式无效",
                "采集结果不符合当前数据契约，未写入数据库。",
                "保留原始采集文件并查看诊断日志；修复采集端后重新采集。",
                false,
                error),
            WorkflowEventParseException or JsonException or FormatException => Failure(
                "protocol_data_invalid",
                ApplicationErrorCategory.Contract,
                "流程数据格式无效",
                "流程输出无法按当前协议读取。",
                "检查 Sidecar 与原生应用版本是否一致。",
                false,
                error),
            SchemaMigrationException => Failure(
                "database_migration_failed",
                ApplicationErrorCategory.Database,
                "数据库迁移失败",
                "数据库未能升级到当前版本。",
                "保留迁移备份并在诊断页查看详细错误。",
                false,
                error),
            SqliteException => Failure(
                "database_operation_failed",
                ApplicationErrorCategory.Database,
                "数据库操作失败",
                "SQLite 无法完成本次操作。",
                "确认数据目录可访问且数据库未被其他程序占用，然后重试。",
                true,
                error),
            UnauthorizedAccessException => Failure(
                "path_access_denied",
                ApplicationErrorCategory.Environment,
                "没有文件访问权限",
                "当前账户无法访问所需文件或目录。",
                "在系统设置中选择可写目录，或调整目录权限。",
                false,
                error),
            FileNotFoundException or DirectoryNotFoundException => Failure(
                "required_file_missing",
                ApplicationErrorCategory.Environment,
                "缺少运行文件",
                "任务依赖的文件或目录不存在。",
                "运行环境检查并修复安装或数据目录。",
                false,
                error),
            TimeoutException or HttpRequestException => Failure(
                "environment_unreachable",
                ApplicationErrorCategory.Environment,
                "外部环境无响应",
                "浏览器、EMS 或本地服务暂时无法访问。",
                "确认网络和采集浏览器状态后重试。",
                true,
                error),
            ArgumentException => Failure(
                "invalid_configuration",
                ApplicationErrorCategory.Configuration,
                "配置或输入无效",
                "当前设置或输入值不符合要求。",
                "检查页面中的输入项和系统设置。",
                false,
                error),
            IOException => Failure(
                "storage_operation_failed",
                ApplicationErrorCategory.Environment,
                "文件操作失败",
                "无法读取或写入任务文件。",
                "确认磁盘空间、目录权限和文件占用状态后重试。",
                true,
                error),
            _ => Failure(
                "internal_unexpected_error",
                ApplicationErrorCategory.Internal,
                "发生未预期错误",
                "程序未能完成当前操作。",
                "在诊断页保留日志后重试；若重复出现，请根据错误代码排查。",
                false,
                error),
        };
    }

    private static ApplicationFailure ClassifyWorkflow(WorkflowExecutionException exception)
    {
        return exception.Outcome switch
        {
            WorkflowTerminalOutcome.Rejected => Failure(
                "collection_quality_rejected",
                ApplicationErrorCategory.Quality,
                "采集结果未通过质量门",
                "采集结果存在阻断问题，当前数据未更新。",
                "查看质量日志并补采对应楼栋或子区。",
                true,
                exception),
            WorkflowTerminalOutcome.AuthRequired => Failure(
                "ems_authentication_required",
                ApplicationErrorCategory.Authentication,
                "需要登录 EMS",
                "采集浏览器中没有可用的 EMS 登录状态。",
                "打开采集浏览器并完成登录后重试。",
                true,
                exception),
            WorkflowTerminalOutcome.Cancelled => Failure(
                "collection_cancelled",
                ApplicationErrorCategory.Cancelled,
                "采集已取消",
                "Sidecar 已停止当前采集。",
                "可以重新开始采集。",
                true,
                exception),
            WorkflowTerminalOutcome.InternalError => Failure(
                "sidecar_internal_error",
                ApplicationErrorCategory.Collection,
                "采集组件发生内部错误",
                "Sidecar 未能完成本次任务。",
                "查看采集日志，确认环境后重试。",
                true,
                exception),
            _ => Failure(
                "collection_workflow_failed",
                ApplicationErrorCategory.Collection,
                "采集流程失败",
                "采集流程未正常完成。",
                "查看采集日志和失败阶段后重试。",
                true,
                exception),
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? Unwrap(aggregate.InnerExceptions[0])
            : exception;
    }

    private static ApplicationFailure Failure(
        string code,
        ApplicationErrorCategory category,
        string title,
        string userMessage,
        string suggestedAction,
        bool retryable,
        Exception exception)
    {
        return new(
            code,
            category,
            title,
            userMessage,
            suggestedAction,
            retryable,
            exception.GetType().Name + ": " + exception.Message);
    }
}
