from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .approval_status import ApprovalStatus

@dataclass
class ApprovalResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The comment property
    comment: Optional[str] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The decidedAt property
    decided_at: Optional[datetime.datetime] = None
    # The decidedBy property
    decided_by: Optional[UUID] = None
    # The dueAt property
    due_at: Optional[datetime.datetime] = None
    # The escalated property
    escalated: Optional[bool] = None
    # The id property
    id: Optional[UUID] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The runId property
    run_id: Optional[UUID] = None
    # The status property
    status: Optional[ApprovalStatus] = None
    # The stepName property
    step_name: Optional[str] = None
    # The title property
    title: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ApprovalResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ApprovalResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ApprovalResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .approval_status import ApprovalStatus

        from .approval_status import ApprovalStatus

        fields: dict[str, Callable[[Any], None]] = {
            "comment": lambda n : setattr(self, 'comment', n.get_str_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "decidedAt": lambda n : setattr(self, 'decided_at', n.get_datetime_value()),
            "decidedBy": lambda n : setattr(self, 'decided_by', n.get_uuid_value()),
            "dueAt": lambda n : setattr(self, 'due_at', n.get_datetime_value()),
            "escalated": lambda n : setattr(self, 'escalated', n.get_bool_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "runId": lambda n : setattr(self, 'run_id', n.get_uuid_value()),
            "status": lambda n : setattr(self, 'status', n.get_enum_value(ApprovalStatus)),
            "stepName": lambda n : setattr(self, 'step_name', n.get_str_value()),
            "title": lambda n : setattr(self, 'title', n.get_str_value()),
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
        writer.write_str_value("comment", self.comment)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_datetime_value("decidedAt", self.decided_at)
        writer.write_uuid_value("decidedBy", self.decided_by)
        writer.write_datetime_value("dueAt", self.due_at)
        writer.write_bool_value("escalated", self.escalated)
        writer.write_uuid_value("id", self.id)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_uuid_value("runId", self.run_id)
        writer.write_enum_value("status", self.status)
        writer.write_str_value("stepName", self.step_name)
        writer.write_str_value("title", self.title)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

