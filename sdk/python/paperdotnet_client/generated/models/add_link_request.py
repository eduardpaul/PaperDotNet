from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .task_link_kind import TaskLinkKind

@dataclass
class AddLinkRequest(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The itemId property
    item_id: Optional[UUID] = None
    # How two items are related (TSK-02, TSK-06).
    kind: Optional[TaskLinkKind] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> AddLinkRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: AddLinkRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return AddLinkRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .task_link_kind import TaskLinkKind

        from .task_link_kind import TaskLinkKind

        fields: dict[str, Callable[[Any], None]] = {
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_enum_value(TaskLinkKind)),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
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
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_enum_value("kind", self.kind)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

