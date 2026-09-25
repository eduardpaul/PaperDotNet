from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .run_response_outcomes import RunResponse_outcomes
    from .run_status import RunStatus

@dataclass
class RunResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The completedAt property
    completed_at: Optional[datetime.datetime] = None
    # The error property
    error: Optional[str] = None
    # The id property
    id: Optional[UUID] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The outcomes property
    outcomes: Optional[RunResponse_outcomes] = None
    # The startedAt property
    started_at: Optional[datetime.datetime] = None
    # The startedBy property
    started_by: Optional[UUID] = None
    # The status property
    status: Optional[RunStatus] = None
    # The workflow property
    workflow: Optional[str] = None
    # The workflowId property
    workflow_id: Optional[UUID] = None
    # The workflowVersion property
    workflow_version: Optional[int] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> RunResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: RunResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return RunResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .run_response_outcomes import RunResponse_outcomes
        from .run_status import RunStatus

        from .run_response_outcomes import RunResponse_outcomes
        from .run_status import RunStatus

        fields: dict[str, Callable[[Any], None]] = {
            "completedAt": lambda n : setattr(self, 'completed_at', n.get_datetime_value()),
            "error": lambda n : setattr(self, 'error', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "outcomes": lambda n : setattr(self, 'outcomes', n.get_object_value(RunResponse_outcomes)),
            "startedAt": lambda n : setattr(self, 'started_at', n.get_datetime_value()),
            "startedBy": lambda n : setattr(self, 'started_by', n.get_uuid_value()),
            "status": lambda n : setattr(self, 'status', n.get_enum_value(RunStatus)),
            "workflow": lambda n : setattr(self, 'workflow', n.get_str_value()),
            "workflowId": lambda n : setattr(self, 'workflow_id', n.get_uuid_value()),
            "workflowVersion": lambda n : setattr(self, 'workflow_version', n.get_int_value()),
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
        writer.write_datetime_value("completedAt", self.completed_at)
        writer.write_str_value("error", self.error)
        writer.write_uuid_value("id", self.id)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_object_value("outcomes", self.outcomes)
        writer.write_datetime_value("startedAt", self.started_at)
        writer.write_uuid_value("startedBy", self.started_by)
        writer.write_enum_value("status", self.status)
        writer.write_str_value("workflow", self.workflow)
        writer.write_uuid_value("workflowId", self.workflow_id)
        writer.write_int_value("workflowVersion", self.workflow_version)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

