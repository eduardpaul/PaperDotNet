from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .flow_definition import FlowDefinition
    from .json_object import JsonObject
    from .workflow_step import WorkflowStep
    from .workflow_trigger import WorkflowTrigger

@dataclass
class WorkflowResponse(AdditionalDataHolder, Parsable):
    """
    A workflow with the definition of its current `version` (runs keep the version they started with): `steps`or a `flow`, the initial `variables`, and `concurrency` (runs on the same item: `parallel`,`skip` or `replace`).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The builtIn property
    built_in: Optional[str] = None
    # The concurrency property
    concurrency: Optional[str] = None
    # The condition property
    condition: Optional[str] = None
    # The copiedFrom property
    copied_from: Optional[str] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The description property
    description: Optional[str] = None
    # The enabled property
    enabled: Optional[bool] = None
    # The flow property
    flow: Optional[FlowDefinition] = None
    # The id property
    id: Optional[UUID] = None
    # The name property
    name: Optional[str] = None
    # The ETag for `If-Match` on changes (the same as the `ETag` header).
    odata_etag: Optional[str] = None
    # The steps property
    steps: Optional[list[WorkflowStep]] = None
    # When a workflow runs: `type` is `manual` (started by a person), an item event (`itemAdded`,`itemUpdated`, `itemDeleted`, `itemRestored`), `schedule`, `date`, a module trigger(`document.processed`, `approval.decided`, `task.completed`, `comment.added`) or an extensiontrigger. `list` and `contentType` narrow it by name; `changedFields` (updates) needs one of them tochange; `terms` (term paths `Group/Set/Term`) needs the item to have one of them or a term below.`schedule` runs on `cron` (5 fields) in `timeZone` (default: the organization's). `date` runs foreach item of `list` when its date `field` plus `offsetHours` (negative: before) is reached. `manual`may describe the `inputs` a person gives when starting it (a JSON Schema object; they become run variables).
    trigger: Optional[WorkflowTrigger] = None
    # The updatedAt property
    updated_at: Optional[datetime.datetime] = None
    # The variables property
    variables: Optional[JsonObject] = None
    # The version property
    version: Optional[int] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> WorkflowResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: WorkflowResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return WorkflowResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .flow_definition import FlowDefinition
        from .json_object import JsonObject
        from .workflow_step import WorkflowStep
        from .workflow_trigger import WorkflowTrigger

        from .flow_definition import FlowDefinition
        from .json_object import JsonObject
        from .workflow_step import WorkflowStep
        from .workflow_trigger import WorkflowTrigger

        fields: dict[str, Callable[[Any], None]] = {
            "builtIn": lambda n : setattr(self, 'built_in', n.get_str_value()),
            "concurrency": lambda n : setattr(self, 'concurrency', n.get_str_value()),
            "condition": lambda n : setattr(self, 'condition', n.get_str_value()),
            "copiedFrom": lambda n : setattr(self, 'copied_from', n.get_str_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "flow": lambda n : setattr(self, 'flow', n.get_object_value(FlowDefinition)),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "@odata.etag": lambda n : setattr(self, 'odata_etag', n.get_str_value()),
            "steps": lambda n : setattr(self, 'steps', n.get_collection_of_object_values(WorkflowStep)),
            "trigger": lambda n : setattr(self, 'trigger', n.get_object_value(WorkflowTrigger)),
            "updatedAt": lambda n : setattr(self, 'updated_at', n.get_datetime_value()),
            "variables": lambda n : setattr(self, 'variables', n.get_object_value(JsonObject)),
            "version": lambda n : setattr(self, 'version', n.get_int_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_uuid_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("builtIn", self.built_in)
        writer.write_str_value("concurrency", self.concurrency)
        writer.write_str_value("condition", self.condition)
        writer.write_str_value("copiedFrom", self.copied_from)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_str_value("description", self.description)
        writer.write_bool_value("enabled", self.enabled)
        writer.write_object_value("flow", self.flow)
        writer.write_uuid_value("id", self.id)
        writer.write_str_value("name", self.name)
        writer.write_str_value("@odata.etag", self.odata_etag)
        writer.write_collection_of_object_values("steps", self.steps)
        writer.write_object_value("trigger", self.trigger)
        writer.write_datetime_value("updatedAt", self.updated_at)
        writer.write_object_value("variables", self.variables)
        writer.write_int_value("version", self.version)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

