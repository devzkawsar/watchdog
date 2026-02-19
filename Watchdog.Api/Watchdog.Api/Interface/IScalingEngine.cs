using Watchdog.Api.Data;

namespace Watchdog.Api.Interface;


public interface IScalingEngine
{
    Task CheckAndScaleApplications();
    Task ScaleApplication(string applicationId, int desiredInstances);
    Task ScaleDownApplication(string applicationId, int instancesToRemove);
    Task ScaleDownApplication(IEnumerable<ApplicationInstance> instances, int instancesToRemove);
    Task ScaleUpApplication(string applicationId, int instancesToAdd);
    Task ScaleUpApplication(Application application, int instancesToAdd);
}
