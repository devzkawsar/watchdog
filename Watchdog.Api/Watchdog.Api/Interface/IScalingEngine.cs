namespace Watchdog.Api.Interface;


public interface IScalingEngine
{
	Task CheckAndScaleApplications();
	Task ScaleApplication(string applicationId, int desiredInstances);
	Task ScaleDownApplication(string applicationId, int instancesToRemove);
	Task ScaleUpApplication(string applicationId, int instancesToAdd);
}
