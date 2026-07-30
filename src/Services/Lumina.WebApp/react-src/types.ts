export type ApiResponse<T> = {
  success: boolean;
  data: T;
  error?: string;
  message?: string;
};

export type Status = string;

export interface Pipeline {
  id: string;
  name: string;
  description: string;
  status: Status;
  createdBy: string;
  createdAt: string;
  updatedAt?: string;
  stepCount: number;
  steps?: PipelineStep[];
  tags?: string[];
  gitRepoUrl?: string;
  gitBranch?: string;
  specPath?: string;
  specContent?: string;
  buildImage?: string;
  targetDistribution?: string;
  targetRelease?: string;
  targetArchitecture?: string;
  buildProfile?: string;
}

export interface PipelineStep {
  id?: string;
  type: string;
  name: string;
  order: number;
  configuration: Record<string, string>;
}

export interface Build {
  id: string;
  pipelineId: string;
  status: Status;
  specName: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  triggeredBy: string;
  logs?: string;
  sourceUrl?: string;
  commitSha?: string;
  branch?: string;
  commitMessage?: string;
  commitAuthor?: string;
  artifacts?: Artifact[];
  stepRuns?: BuildStep[];
  targetDistribution?: string;
  targetRelease?: string;
  targetArchitecture?: string;
  runnerImageReference?: string;
  runnerImageDigest?: string;
}

export interface BuildStep {
  id: string;
  type: string;
  name: string;
  order: number;
  status: Status;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

export interface Artifact {
  id: string;
  fileName: string;
  fileSize: number;
  hashSha256?: string;
  signingKeyFingerprint?: string;
  cveScanStatus: Status;
}

export interface Repository {
  id: string;
  name: string;
  displayName: string;
  basePath: string;
  arch: string;
  distribution: string;
  isActive: boolean;
  createdAt: string;
  packageCount: number;
}

export interface Scan {
  id: string;
  artifactId: string;
  scannerType: string;
  status: Status;
  totalVulnerabilities: number;
  criticalCount: number;
  highCount: number;
  createdAt: string;
  completedAt?: string;
}

export interface HashRecord {
  artifactId: string;
  hashSha256: string;
  hashSha1: string;
  hashMd5: string;
  computedAt: string;
}

export interface SourcePackage {
  packageId: string;
  packageName: string;
  revision: number;
  isEnabled: boolean;
  sourceUrl: string;
  sourceType: string;
  sourceBranch?: string;
  expectedSha256?: string;
  specPath?: string;
  buildImage?: string;
  status: Status;
  errorMessage?: string;
  fileSize?: number;
  lastFetchedAt?: string;
}

export interface Paginated<T> {
  totalCount: number;
  page: number;
  pageSize: number;
  pipelines?: T[];
  builds?: T[];
  scans?: T[];
  hashes?: T[];
}
